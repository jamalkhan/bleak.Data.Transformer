# ETL Config Schema (`bleak.Data.Transformer`)

A single `.etlconfig` file describes an **Extract → Transform → Load** run: a pool of aliased **sources**
feeding any number of **outputs**. It is consumed by the `ExtractTransformLoad` console command (which
lists the bundled configs in `data/etlconfig` to pick from — or accepts a path to your own — then prompts
for the output folder) and by `EtlEngine.Run(...)`. `.etlconfig` files are JSON; comments (`//`, `/* */`)
and trailing commas are allowed.

## Top level

```jsonc
{
  "version": 1,
  "run": { "joinStrategy": "hashInMemory", "onError": "skip" },
  "sources": { "<alias>": { ...SourceConfig... } },
  "outputs": [ { ...OutputConfig... } ]
}
```

### `run`
| field | values | default | meaning |
|---|---|---|---|
| `joinStrategy` | `hashInMemory` \| `externalMerge` | `hashInMemory` | **hashInMemory**: stream the primary, index children in RAM (any depth). **externalMerge**: disk-backed sort+merge for the primary and its direct children (bounded memory; keys compared as ordinal strings so single/composite/non-numeric all work; all direct children must share the parent key column(s); output is key-sorted). Levels below the top are nested in RAM. |
| `onError` | `skip` \| `abort` \| `nullify` | `skip` | What to do when a step raises a row-level exception. **skip**: drop the row, log it, append it to `<base>_<ts>_rejects.csv`. **abort**: stop the run. **nullify**: omit only the failing field, keep the row. |
| `emitEmpty` | `true` \| `false` | `false` | When `false`, fields whose value is an empty string are omitted. When `true`, blank string cells are kept and emitted as `""` (e.g. a blank `middle_name`). Null values and empty arrays are always omitted. |

## `sources` — alias → source
```jsonc
"corp": {
  "file": "customers.csv",  // relative to the config file (absolute allowed)
  "format": "delimited",                  // "delimited" only today; json/xml reserved
  "delimiter": "csv",                     // csv|comma "," · tsv|tab "\t" · pipe "|" · semicolon ";" · or any single char
  "quote": "\"",
  "hasHeader": true,
  "fields": {                             // optional input typing; unlisted columns default to string
    "customer_key": { "type": "integer" },
    "acquisition_date": { "type": "date", "format": "yyyy-MM-dd", "culture": "en-US" }
  }
}
```
Input fields are **typed but never transformed** (transforms live on outputs).

**Paths** — `file` (here) and `load.folder` (below) support a leading `~` (home directory) and resolve
relative to the **config file's folder** when not absolute.

**Output folder** — set `load.folder` per output (`~` and relative paths allowed). When every output
sets one, the command does not prompt for an output folder; otherwise it prompts for a fallback.

## `outputs` — one feed each

Every output is the **root node** of a tree (`source` + `fields` + `children`), plus a `mode` and `load`.

### `mode: "object"` — one JSON object per primary row
```jsonc
{
  "name": "userprops", "mode": "object", "source": "corp",
  "fields": [ { "name": "...", "from": "<column>", "type": "<type>", "pipeline": [ ...steps... ] } ],
  "children": [
    { "source": "brand", "as": "brand_profile", "cardinality": "many",
      "on": { "parent": "customer_key", "child": "customer_key" },
      "fields": [ ... ],
      "children": [ ... ]   // arbitrary depth
    }
  ],
  "load": { "format": "jsonl", "folder": "~/Desktop", "fileBaseName": "userprops", "splitEvery": 1000000 }
}
```
`cardinality`: `one` nests an object (omitted when no match); `many` nests an array.

**Composite join keys** — match on more than one column with `on.parents`/`on.children` (positionally
matched) instead of `on.parent`/`on.child`:
```jsonc
"on": { "parents": ["customer_key", "brand"], "children": ["customer_key", "brand"] }
```
Supported by `hashInMemory` (and below the top level under `externalMerge`, which keys the top level on a single integer column).

**Combining sources into one array (shared `as`).** Two or more child relationships that declare the
same `as` are **concatenated into a single array**, in declaration order. This is how heterogeneous rows
land in one list — e.g. email rows (`brand_contact`) and phone rows (`brand_phone_number`) both with `"as": "contacts"`:
```jsonc
"children": [
  { "source": "brand_contact", "as": "contacts", "cardinality": "many", "on": { "parent": "customer_key", "child": "customer_key" }, "fields": [ /* contact_type:"email", ... */ ] },
  { "source": "brand_phone_number", "as": "contacts", "cardinality": "many", "on": { "parent": "customer_key", "child": "customer_key" }, "fields": [ /* contact_type:"phone", ... */ ] }
]
```

**Output field order is the config order.** Within a node, properties are emitted in the exact order
fields are listed, followed by its node-level relationships. Reorder the `fields` array to reorder the
output. To place an array-of-objects at a specific position (not after all scalar fields), use an
**inline relationship field** (below).

**`distinctBy` / `limit`** (on any relationship): keep only the first child per distinct value of a column,
and/or cap the count. e.g. `"distinctBy": "external_cust_id", "limit": 1` = "first available, deduped".

### Inline relationship fields (array of objects at a position)
A field with a `source` is a relationship rendered **inline as an array of objects**, exactly where you
list it (so it can sit between scalar fields). Its `fields` describe each element; `distinctBy`/`limit`
apply. This is how you emit, say, a typed-id list:
```jsonc
{ "name": "external_ids", "type": "array",
  "source": "brand_contact", "on": { "parent": "customer_key", "child": "customer_key" },
  "distinctBy": "external_cust_id", "limit": 1,
  "fields": [
    { "name": "name",  "type": "string", "pipeline": [ { "op": "concat", "values": [ { "literal": "ext_id" } ] } ] },
    { "name": "value", "from": "external_cust_id", "type": "string" }
  ] }
// -> "external_ids": [ { "name": "ext_id", "value": "<id>" } ]
```
`"caseInsensitiveJoin": true` makes the join ignore case on both sides, same as on a child relationship.

**Several inline fields with the same `name`** render as ONE array, concatenated in declaration order. Set
`"shareDistinct": true` on those that should share a single distinct set (by each one's `distinctBy` value) and a
single `limit`, so a value arriving from two sources (e.g. the same `external_customer_id` in the email and
phone files) is emitted once and the whole group is capped once. Fields without it keep per-source behavior.

**Inside a merged group** (a relationship with `mergeBy`): an inline array field in e.g. `contact_properties`
is **never prefixed** by `mergePrefix`. It is written once, as the union of every merged row's matches, with
a child row reached through several merged rows kept once and `distinctBy` applied across the whole union —
so a parent-level contact merged from several brands gets ONE `payment_cards` array of all its
distinct cards, not `b1_payment_cards` / `b2_payment_cards`.

### `mode: "keyedMap"` — one object per group, with dynamic property keys
```jsonc
{
  "name": "contacts", "mode": "keyedMap", "source": "email",
  "groupBy": "customer_key",
  "keyExpr": { "pipeline": [ { "op": "concat", "values": [ {"literal":"email::"}, {"field":"email_address"} ] } ] },
  "dedup": { "within": "email_address", "keepMaxOf": "email_status_date" },   // optional; default: no dedup
  "fields": [ ... ],   // the per-key value object
  "load": { ... }
}
```

### `filter` — drop records before they're written
An output may declare a `filter`: conditions the **primary record** must satisfy (all ANDed) to be emitted.
Failing records are excluded and counted; set `filterExcludedFile: true` to also write them to
`{fileBaseName}_{timestamp}_filtered.csv`.
```jsonc
"filter": [ { "column": "order_datetime", "op": "onOrAfter", "value": "now-2years" } ],
"filterExcludedFile": false
```
Ops: `onOrAfter`/`after`/`onOrBefore`/`before` (date; `value` is an ISO date or a relative token like
`now`, `now-2years`, `now-90days` — resolved at run start) · `eq`/`ne`/`gt`/`ge`/`lt`/`le` · `in` (array) ·
`empty`/`notEmpty`. A missing/unparseable date fails a date condition.

### `requireRelations` — drop records with no matching children (object mode)
Emit a primary record only when **at least one** of the named relations (child `as` names on the primary
node) has content after its own `filter` — for a keyed relation, at least one row with a non-empty key.
E.g. corp customers that have at least one app contact:
```jsonc
"source": "corp",
"requireRelations": [ "site_contacts" ]
```
Failing records count as filtered (and go to the `_filtered.csv` when `filterExcludedFile` is set). Checked
after `filter` and before `distinctBy`. An unknown relation name fails at compile time.

## Fields & types
`type` is the emitted JSON type. Supported: `string`, `integer` (64-bit), `decimal`, `double`, `bool`,
`date`, `datetime`, plus `object` (an object-group) and `array` (a JSON array, e.g. from `split` or an
array lookup). **Null/empty values are omitted** from the output object (an empty array is omitted too).
`date` emits `yyyy-MM-dd`, `datetime` emits ISO-8601 (`o`). A scalar field's pipeline starts from the typed
`from` value (or null if `from` is omitted) and must end at a value compatible with `type` (the engine coerces).

### Object-group fields (`type: "object"`)
Emit a nested object built from the **same row** — its own `fields` and, optionally, relationships in
`children` (joined against the current node's row, rendered inside the group). Nestable.
```jsonc
{ "name": "contact_properties", "type": "object",
  "fields": [
    { "name": "brand", "from": "brand", "type": "string" },
    { "name": "email_key", "from": "email_key", "type": "string" }
  ],
  "children": [
    { "source": "loyalty", "as": "loyalty", "cardinality": "many",
      "on": { "parent": "email_key", "child": "loyalty_email_key" },
      "fields": [ { "name": "loyalty_tier", "from": "loyalty_tier", "type": "string" } ] }
  ] }
```

### Lookup fields
Pull a value from a **related source** (joined on a key) instead of the current row — for values that don't
live on the primary row. Builds an in-memory index over the lookup source.
```jsonc
// single scalar (first match) — e.g. a date_of_birth that lives on a child table
{ "name": "date_of_birth", "type": "string",
  "lookup": { "source": "brand", "on": { "parent": "customer_key", "child": "customer_key" },
              "column": "date_of_birth", "single": true } }

// array of values — e.g. a deduped list of ids; limit:1 = "first available"
{ "name": "external_cust_id", "type": "array",
  "lookup": { "source": "email", "on": { "parent": "customer_key", "child": "customer_key" },
              "column": "external_cust_id", "distinct": true, "limit": 1 } }
```
| lookup field | meaning |
|---|---|
| `source` | the source alias to read from |
| `on.parent` / `on.child` | join: a column on the current row / a column on the lookup source |
| `column` | the lookup source column to project |
| `single` | `true` → one scalar (first match, coerced to the field `type`); `false` → an array |
| `distinct` | array only: drop duplicate values (first-seen order) |
| `limit` | array only: cap the count (`1` = first available) |

Empty values are skipped; a key with no matches yields null (so the field is omitted).

## Transform pipeline
Steps run in order; each step's output is the next step's input. Multi-input ops name extra operands
explicitly. An **operand** is one of `{ "field": "<col>" }`, `{ "literal": <json> }`, or `{ "value": true }`
(the running value).

| op | params | effect |
|---|---|---|
| `toString` `toInteger`/`toInt` `toDecimal` `toDouble` `toBool` `toDate` `toDateTime` | `format?`, `culture?` (date) | convert the running value |
| `dateFormat` | `format` (req), `culture?` | format a date/datetime → string |
| `numberFormat` | `format` (req), `culture?` | format a number → string (e.g. `"N2"`, `"F2"`, `"P1"`) |
| `add` `subtract` `multiply` `divide` | `rhs` (operand) | `runningValue <op> rhs` (decimal, or double if either side is floating) |
| `concat` | `values` (operands), `separator?` | join operands into a string |
| `split` | `separator?` (default `;`) | split a string into an **array** of strings (trimmed, empties dropped). Use `type: "array"` on the field. |
| `lower` / `upper` | — | lowercase / uppercase the running string |

### Per-step error handling
```jsonc
{ "op": "toInteger", "onError": "throw" }            // default: raise a row-level exception
{ "op": "toInteger", "onError": "null" }             // quietly yield null
{ "op": "toInteger", "onError": { "default": 0 } }   // substitute a value
```
A thrown row-level exception is then handled by `run.onError` (skip / abort / nullify).

### Example
```jsonc
{ "name": "net_points", "from": "total_active_points", "type": "decimal",
  "pipeline": [
    { "op": "toDecimal", "onError": "null" },
    { "op": "subtract", "rhs": { "field": "total_pending_points" } },
    { "op": "multiply", "rhs": { "literal": 1.1 } }
  ] }
```

## Extending
Register a new op via `TransformRegistry.Register("myOp", step => new MyTransform(...))` and pass that
registry to the engine. Sources (`ISourceReader`) and the output writer (`IOutputWriter`) are likewise
pluggable — JSON/XML sources and non-JSONL writers slot in without touching the pipeline.

## Known v1 limitations
- `externalMerge` requires a shared parent key column across direct children, and nests deeper levels
  using in-memory indexes (logged); its output is ordered by the join key.
- Lookups build an in-memory index over their source (fine for dimension tables; mind memory on huge ones).
- A field's value comes from a single source row, the pipeline, or one lookup — there is no cross-row
  aggregation beyond a lookup's `distinct`/`limit`.
