# bleak.Data.Transformer

A **config-driven ETL engine**. You describe — in a single JSON file — which files to read, how to
type their columns, how to join/nest them, what transforms to apply, and where to write the result.
The engine then **E**xtracts the sources, **T**ransforms each row through per-field pipelines, and
**L**oads the output as JSONL.

It is designed for large inputs: sources are read with a fully streaming reader, and joins can run
with bounded memory via disk-backed external sort + merge.

> All examples below use a small invented dataset (customers / orders / line items). Substitute your
> own files and columns.

---

## Table of contents

1. [Concepts](#concepts)
2. [The input files](#the-input-files)
3. [Quick start](#quick-start)
4. [Running the engine](#running-the-engine)
5. [The config file, section by section](#the-config-file-section-by-section)
   - [`run`](#run) · [`sources`](#sources) · [`outputs`](#outputs) · [fields & types](#fields--types) · [relationships](#relationships-nesting) · [transform pipeline](#transform-pipeline) · [`load`](#load)
6. [Worked examples](#worked-examples)
7. [Output format](#output-format)
8. [Join strategies](#join-strategies)
9. [Error handling](#error-handling)
10. [Type reference](#type-reference)
11. [Extending the engine](#extending-the-engine)
12. [Troubleshooting](#troubleshooting)

---

## Concepts

```
                ┌─────────── one JSON config ───────────┐
  files  ──▶    │  sources (aliased, typed)             │
               │       │                                │
               │       ▼                                │
               │  outputs[]  ── relationships (nesting) │
               │       │       fields → transform pipes │
               │       ▼                                │
  JSONL  ◀──    │  load (split into N-record files)     │
                └────────────────────────────────────────┘
```

- **Source** — one input file, given an **alias**. Columns can be **typed** (string, integer, decimal, …).
- **Output** — one feed produced from the sources. It is a **tree of nodes**: a primary source plus
  nested child sources joined on a key (to any depth).
- **Field** — one property in the output. It starts from a source column, optionally runs through a
  **transform pipeline**, and is emitted as its declared type.
- **Transform** — a step that converts/formats/computes a value. Steps **chain**: each step's output is
  the next step's input.

Input fields are **typed but never transformed**; transforms live only on output fields.

---

## The input files

The engine reads **delimited** text files (CSV, TSV, pipe, semicolon, or any single-character
delimiter). A header row is expected by default. Example files for this guide:

`customers.csv`
```csv
customer_id,first_name,last_name,email,signup_date,lifetime_value
1001,Ada,Lovelace,ada@example.com,2021-03-15,2540.50
1002,Alan,Turing,alan@example.com,2020-11-02,180.00
1003,Grace,Hopper,grace@example.com,2022-07-21,
```

`orders.tsv` (tab-separated)
```
order_id	customer_id	order_date	subtotal	discount
9001	1001	2023-01-10	100.00	10.00
9002	1001	2023-02-14	50.00	0
9003	1002	2023-03-01	200.00	25.00
```

`line_items.csv`
```csv
order_id,sku,quantity,unit_price
9001,WIDGET-A,2,30.00
9001,WIDGET-B,1,40.00
9002,WIDGET-A,5,10.00
9003,GADGET-Z,1,200.00
```

---

## Quick start

The smallest useful config: read one file, type a few columns, write JSONL.

`etl.quickstart.etlconfig`
```jsonc
{
  "version": 1,
  "sources": {
    "customers": {
      "file": "customers.csv",
      "fields": {
        "customer_id":    { "type": "integer" },
        "signup_date":    { "type": "date", "format": "yyyy-MM-dd" },
        "lifetime_value": { "type": "decimal" }
      }
    }
  },
  "outputs": [
    {
      "name": "customers",
      "mode": "object",
      "source": "customers",
      "fields": [
        { "name": "id",            "from": "customer_id",    "type": "integer" },
        { "name": "email",         "from": "email",          "type": "string"  },
        { "name": "joined",        "from": "signup_date",    "type": "date"    },
        { "name": "lifetime_value","from": "lifetime_value", "type": "decimal" }
      ],
      "load": { "fileBaseName": "customers", "splitEvery": 1000000 }
    }
  ]
}
```

Output (one JSON object per line; note `1003`'s empty `lifetime_value` is omitted):
```json
{"id":1001,"email":"ada@example.com","joined":"2021-03-15","lifetime_value":2540.50}
{"id":1002,"email":"alan@example.com","joined":"2020-11-02","lifetime_value":180.00}
{"id":1003,"email":"grace@example.com","joined":"2022-07-21"}
```

---

## Running the engine

### Option A — the console command

In **DataMonger**, choose **“Extract / Transform / Load (config-driven)”**. It then:

1. **lists the bundled `.etlconfig` files** (from `data/etlconfig`) with a number each — pick one by
   number, or type the **path to your own `.etlconfig`** file; then
2. prompts for **the output folder** — *unless* every output sets its own `load.folder`, in which case
   there's no prompt.

Everything else (input files, split size, join strategy, error policy) comes from the config. Source
file paths in the config are resolved **relative to the config file's own folder** (absolute paths are
also accepted). Config files use the `.etlconfig` extension but are JSON (jsonc — comments and trailing
commas allowed).

### Option B — programmatically

```csharp
using bleak.Data.Transformer.Config;
using bleak.Data.Transformer.Engine;

var configPath = "/path/to/etl.quickstart.etlconfig";
var config     = EtlConfigLoader.FromFile(configPath);
var configDir  = Path.GetDirectoryName(Path.GetFullPath(configPath))!;  // base for relative file paths
var timestamp  = DateTime.Now.ToString("yyyyMMdd_HHmmss");

IReadOnlyList<EtlOutputResult> results =
    EtlEngine.Run(config, configDir, outputFolder: "/path/to/out", timestamp, log: Console.WriteLine);

foreach (var r in results)
    Console.WriteLine($"{r.Name}: {r.Records:N0} records, {r.Files.Count} file(s), {r.Rejected:N0} skipped");
```

`EtlConfigLoader` accepts JSON **with comments and trailing commas** (jsonc), so your config can be
annotated. Each `EtlOutputResult` reports `Name`, `Records`, `Files` (final paths), and `Rejected`.

---

## The config file, section by section

Top-level shape:

```jsonc
{
  "version": 1,
  "run":     { /* run-wide settings */ },
  "sources": { /* alias -> source */ },
  "outputs": [ /* one entry per feed */ ]
}
```

### `run`

| field | values | default | meaning |
|---|---|---|---|
| `joinStrategy` | `hashInMemory` · `externalMerge` | `hashInMemory` | How relationships are joined. See [Join strategies](#join-strategies). |
| `onError` | `skip` · `abort` · `nullify` | `skip` | What to do when a row hits a fatal transform error. See [Error handling](#error-handling). |
| `emitEmpty` | `true` · `false` | `false` | `false` omits empty-string fields; `true` keeps them, emitting `""` (e.g. a blank `middle_name`). Null values and empty arrays are always omitted. |

```jsonc
"run": { "joinStrategy": "externalMerge", "onError": "skip" }
```

### `sources`

A map of **alias → source**. The alias is how outputs and relationships refer to the file.

```jsonc
"sources": {
  "customers": {
    "file": "customers.csv",   // relative to the config file (absolute allowed)
    "format": "delimited",     // "delimited" today; "json"/"xml" reserved
    "delimiter": "csv",        // see table below
    "quote": "\"",             // text qualifier
    "hasHeader": true,         // first row is the header
    "fields": {                // OPTIONAL input typing; unlisted columns stay strings
      "customer_id":    { "type": "integer" },
      "signup_date":    { "type": "date", "format": "yyyy-MM-dd", "culture": "en-US" },
      "lifetime_value": { "type": "decimal" }
    }
  },
  "orders":     { "file": "orders.tsv", "delimiter": "tsv",
                  "fields": { "order_id": { "type": "integer" }, "customer_id": { "type": "integer" },
                              "subtotal": { "type": "decimal" }, "discount": { "type": "decimal" } } },
  "line_items": { "file": "line_items.csv",
                  "fields": { "order_id": { "type": "integer" }, "quantity": { "type": "integer" },
                              "unit_price": { "type": "decimal" } } }
}
```

**Delimiter values** (shorthand or a literal character):

| value | delimiter |
|---|---|
| `csv` · `comma` · `,` | comma |
| `tsv` · `tab` · `\t` | tab |
| `pipe` · `\|` | pipe |
| `semicolon` · `;` | semicolon |
| any single character | that character |

**Input field typing.** Each entry under `fields` declares a column's type so the value is parsed once
on read. `date`/`datetime` may add a `format` (a .NET format string; ISO-8601 is assumed if omitted) and
a `culture` (invariant if omitted). Empty cells become null. **Input fields carry no transforms.**

### `outputs`

Each output is the **root node** of a tree: `source` + `fields` + optional `children`, plus a `mode`
and a `load` block. There are two modes.

#### `mode: "object"` — one JSON object per primary row

```jsonc
{
  "name": "customer_orders",
  "mode": "object",
  "source": "customers",
  "fields":   [ /* ...root fields... */ ],
  "children": [ /* ...nested relationships... */ ],
  "load":     { "fileBaseName": "customer_orders", "splitEvery": 1000000 }
}
```

#### `mode: "keyedMap"` — one object per group, keyed by a dynamic expression

```jsonc
{
  "name": "emails_by_customer",
  "mode": "keyedMap",
  "source": "customers",
  "groupBy": "customer_id",
  "keyExpr": { "pipeline": [ { "op": "concat", "values": [ { "literal": "email::" }, { "field": "email" } ] } ] },
  "dedup":   { "within": "email", "keepMaxOf": "signup_date" },   // optional; default: no dedup
  "fields":  [ /* ...the value object for each key... */ ],
  "load":    { "fileBaseName": "emails_by_customer", "splitEvery": 1000000 }
}
```

`keyedMap` groups the primary source by `groupBy`, then for each row emits a property whose **name** is
the `keyExpr` pipeline result and whose **value** is the mapped `fields` object. `dedup` is optional:
within each group it keeps, per distinct `within` value, the row with the greatest `keepMaxOf`
(string/ISO-date comparison).

### Fields & types

```jsonc
{ "name": "joined", "from": "signup_date", "type": "date", "pipeline": [ /* optional steps */ ] }
```

| key | meaning |
|---|---|
| `name` | the output JSON property name |
| `from` | source column the pipeline starts from. **May be omitted** for fully computed fields (the pipeline then starts from `null`). |
| `type` | the emitted JSON type — `string`, `integer`, `decimal`, `double`, `bool`, `date`, `datetime`, plus `object` (object-group) and `array` |
| `pipeline` | optional ordered list of transform steps |

The pipeline result is **coerced to `type`** before writing. **Null or empty values are omitted** from
the output object (an empty array is omitted too). `date` emits `yyyy-MM-dd`; `datetime` emits ISO-8601.

**Output field order is the order you list fields** — properties are emitted in `fields` order, then the
node's relationships. Reorder `fields` to reorder the JSON. To place an array-of-objects *between* scalar
fields, use an **inline relationship field** (below).

#### Inline relationship fields (array of objects, positioned)

A field with a `source` renders as an **array of objects right where you list it** (its `fields` describe
each element). `distinctBy` keeps the first element per distinct value of a column; `limit` caps the count.
Any relationship (inline or in `children`) accepts `distinctBy`/`limit`.

```jsonc
{ "name": "client_ids", "type": "array",
  "source": "emails", "on": { "parent": "id", "child": "person_id" },
  "distinctBy": "email_key", "limit": 1,
  "fields": [
    { "name": "name",  "type": "string", "pipeline": [ { "op": "concat", "values": [ { "literal": "ext_id" } ] } ] },
    { "name": "value", "from": "addr", "type": "string" }
  ] }
// -> "client_ids": [ { "name": "ext_id", "value": "ada@example.com" } ]
```

#### Object-group fields (`type: "object"`)

Emit a nested object built from the **same row**: its own `fields`, and optionally relationships in
`children` (joined against the current row, rendered inside the group). Object-groups nest.

```jsonc
{ "name": "address", "type": "object",
  "fields": [
    { "name": "city",  "from": "city",  "type": "string" },
    { "name": "state", "from": "state", "type": "string" }
  ],
  "children": [
    { "source": "shipments", "as": "shipments", "cardinality": "many",
      "on": { "parent": "address_id", "child": "address_id" },
      "fields": [ { "name": "tracking", "from": "tracking", "type": "string" } ] }
  ] }
```

#### Lookup fields

Pull a value from a **related source** (joined on a key) rather than the current row — for values that
don't live on the primary row. The engine builds an in-memory index over the lookup source.

```jsonc
// single scalar: the first match (coerced to the field's type)
{ "name": "region", "type": "string",
  "lookup": { "source": "stores", "on": { "parent": "store_id", "child": "id" },
              "column": "region", "single": true } }

// array of values: distinct, optionally capped (limit:1 = "first available")
{ "name": "channel_ids", "type": "array",
  "lookup": { "source": "orders", "on": { "parent": "customer_id", "child": "customer_id" },
              "column": "channel_id", "distinct": true } }
```

| lookup key | meaning |
|---|---|
| `source` | source alias to read from |
| `on.parent` / `on.child` | join: column on the current row / column on the lookup source |
| `column` | the lookup source column to project |
| `single` | `true` → first match as a scalar; `false` → an array of matches |
| `distinct` | array only: drop duplicates (first-seen order) |
| `limit` | array only: cap the count (`1` = first available) |

### Relationships (nesting)

A `children` entry joins another source to its parent and nests the result. Children may have their own
`children`, to **any depth**.

```jsonc
"children": [
  {
    "source": "orders",            // child source alias
    "as": "orders",                // JSON property to nest under
    "cardinality": "many",         // "many" -> array; "one" -> single object
    "on": { "parent": "customer_id", "child": "customer_id" },
    "fields": [ /* order fields */ ],
    "children": [
      {
        "source": "line_items",
        "as": "items",
        "cardinality": "many",
        "on": { "parent": "order_id", "child": "order_id" },
        "fields": [ /* line-item fields */ ]
      }
    ]
  }
]
```

- `on.parent` is a column on the **parent** node's source; `on.child` is a column on the **child**'s source.
- **Composite (multi-column) joins**: use `on.parents` / `on.children` (positionally matched) to match on more
  than one column — e.g. match a customer's per-brand rows on both keys: `"on": { "parents": ["customer_key","brand"], "children": ["customer_key","brand"] }`. Supported by `hashInMemory` (and below the top level under `externalMerge`).
- `cardinality: "one"` nests a single object (omitted entirely if there is no match); `"many"` nests an
  array (empty `[]` when there are no matches).

**Combining sources into one array (shared `as`).** Two or more child relationships with the **same `as`**
are concatenated into a single array (declaration order). This puts heterogeneous rows in one list — e.g.
email rows and phone rows under one `contacts` array:

```jsonc
"children": [
  { "source": "emails", "as": "contacts", "cardinality": "many", "on": { "parent": "id", "child": "person_id" },
    "fields": [ { "name": "type", "type": "string", "pipeline": [ { "op": "concat", "values": [ { "literal": "email" } ] } ] },
                { "name": "value", "from": "addr", "type": "string" } ] },
  { "source": "phones", "as": "contacts", "cardinality": "many", "on": { "parent": "id", "child": "person_id" },
    "fields": [ { "name": "type", "type": "string", "pipeline": [ { "op": "concat", "values": [ { "literal": "phone" } ] } ] },
                { "name": "value", "from": "number", "type": "string" } ] }
]
```

### Transform pipeline

Steps run top to bottom. Each step receives the **running value** (the previous step's output, or the
field's `from` value for the first step) and returns the next running value. Operations that need more
than one input (arithmetic, concat) name extra **operands** explicitly.

An **operand** is exactly one of:

```jsonc
{ "field": "discount" }     // the typed value of a column on the current row
{ "literal": 1.1 }          // a constant (string / number / bool)
{ "value": true }           // the current running value
```

**Operation catalog:**

| op | params | effect |
|---|---|---|
| `toString` | `culture?` | convert running value → string |
| `toInteger` (alias `toInt`) | — | → 64-bit integer |
| `toDecimal` | — | → decimal (exact) |
| `toDouble` | — | → double (floating) |
| `toBool` | — | → bool (`true`/`false`, also `1`/`0`) |
| `toDate` | `format?`, `culture?` | parse → date |
| `toDateTime` | `format?`, `culture?` | parse → datetime |
| `dateFormat` | `format` (required), `culture?` | format a date/datetime → string |
| `numberFormat` | `format` (required), `culture?` | format a number → string (e.g. `"N2"`, `"F2"`, `"C"`, `"P1"`) |
| `add` `subtract` `multiply` `divide` | `rhs` (operand) | `runningValue <op> rhs` |
| `concat` | `values` (operands), `separator?` | join operands into a string |
| `split` | `separator?` (default `;`) | split a string into an **array** of strings (trimmed, empties dropped); use `type: "array"` |
| `lower` / `upper` | — | lowercase / uppercase the running string |

Arithmetic computes in **decimal** unless either operand is a `double`/`float`, in which case it
computes in `double`.

**Per-step error handling** with `onError`:

```jsonc
{ "op": "toInteger", "onError": "throw" }            // default — raise a row-level error
{ "op": "toInteger", "onError": "null" }             // quietly become null
{ "op": "toInteger", "onError": { "default": 0 } }   // substitute a value
```

A *thrown* row-level error is then handled by the run's [`onError`](#error-handling) policy.

### `load`

```jsonc
"load": { "format": "jsonl", "folder": "~/Desktop", "fileBaseName": "customer_orders", "splitEvery": 1000000 }
```

| field | default | meaning |
|---|---|---|
| `format` | `jsonl` | output format (JSONL today; the writer is pluggable) |
| `folder` | *(prompted)* | output folder for this feed. Supports `~` and paths relative to the config file. When omitted, the command prompts for one (and that prompt is skipped entirely if every output sets a folder). |
| `fileBaseName` | `output` | base of the output file name |
| `splitEvery` | `1000000` | start a new file every N records |

Files are written as `{fileBaseName}_{timestamp}.jsonl`, or `{fileBaseName}_{timestamp}_{n}.jsonl` when
the record count exceeds `splitEvery`.

**Paths** — source `file` paths and `load.folder` both support a leading `~` (home directory) and resolve
relative to the **config file's folder** when not absolute.

### `filter` — drop records before they're written

An output may declare a `filter`: a list of conditions the **primary record** must satisfy (all, ANDed)
to be emitted. Records that fail are excluded and counted (logged at the end). Set `filterExcludedFile: true`
to also append the excluded rows to `{fileBaseName}_{timestamp}_filtered.csv`.

```jsonc
"filter": [
  { "column": "order_datetime", "op": "onOrAfter", "value": "now-2years" }
],
"filterExcludedFile": false
```

| op | meaning |
|---|---|
| `onOrAfter` `after` `onOrBefore` `before` | date compare; `value` is an ISO date or a relative token (`now`, `now-2years`, `now-90days`, `now-12hours`). A missing/unparseable date fails the condition. |
| `eq` `ne` `gt` `ge` `lt` `le` | compare (numeric if both parse as numbers, else ordinal string) |
| `in` | `value` is an array; the column must be one of them |
| `empty` / `notEmpty` | the column is/ isn't blank |

Relative tokens resolve to **run start** time, so `now-2years` ages records out as the clock advances.

---

## Worked examples

### Example A — flat output with a computed field

Compute `net_total = (subtotal − discount)` and a formatted display string, from `orders.tsv`.

```jsonc
{
  "version": 1,
  "sources": {
    "orders": {
      "file": "orders.tsv", "delimiter": "tsv",
      "fields": {
        "order_id":    { "type": "integer" },
        "customer_id": { "type": "integer" },
        "order_date":  { "type": "date", "format": "yyyy-MM-dd" },
        "subtotal":    { "type": "decimal" },
        "discount":    { "type": "decimal" }
      }
    }
  },
  "outputs": [
    {
      "name": "orders", "mode": "object", "source": "orders",
      "fields": [
        { "name": "order_id",    "from": "order_id",    "type": "integer" },
        { "name": "customer_id", "from": "customer_id", "type": "integer" },

        // start from subtotal, subtract the discount column -> net total
        { "name": "net_total", "from": "subtotal", "type": "decimal",
          "pipeline": [ { "op": "subtract", "rhs": { "field": "discount" } } ] },

        // a human-readable string built from the net total (computed again, then formatted)
        { "name": "net_display", "from": "subtotal", "type": "string",
          "pipeline": [
            { "op": "subtract",     "rhs": { "field": "discount" } },
            { "op": "numberFormat", "format": "C2", "culture": "en-US" }
          ] },

        // reformat the date into a different string layout
        { "name": "ordered_on", "from": "order_date", "type": "string",
          "pipeline": [ { "op": "dateFormat", "format": "MMM d, yyyy", "culture": "en-US" } ] }
      ],
      "load": { "fileBaseName": "orders", "splitEvery": 1000000 }
    }
  ]
}
```

Result:
```json
{"order_id":9001,"customer_id":1001,"net_total":90.00,"net_display":"$90.00","ordered_on":"Jan 10, 2023"}
{"order_id":9002,"customer_id":1001,"net_total":50.00,"net_display":"$50.00","ordered_on":"Feb 14, 2023"}
{"order_id":9003,"customer_id":1002,"net_total":175.00,"net_display":"$175.00","ordered_on":"Mar 1, 2023"}
```

### Example B — nested object, two levels deep

Customers → their orders → each order's line items.

```jsonc
{
  "version": 1,
  "run": { "joinStrategy": "externalMerge", "onError": "skip" },
  "sources": {
    "customers":  { "file": "customers.csv",  "fields": { "customer_id": { "type": "integer" }, "lifetime_value": { "type": "decimal" } } },
    "orders":     { "file": "orders.tsv", "delimiter": "tsv",
                    "fields": { "order_id": { "type": "integer" }, "customer_id": { "type": "integer" },
                                "subtotal": { "type": "decimal" }, "discount": { "type": "decimal" } } },
    "line_items": { "file": "line_items.csv",
                    "fields": { "order_id": { "type": "integer" }, "quantity": { "type": "integer" }, "unit_price": { "type": "decimal" } } }
  },
  "outputs": [
    {
      "name": "customer_orders", "mode": "object", "source": "customers",
      "fields": [
        { "name": "customer_id",    "from": "customer_id",    "type": "integer" },
        { "name": "name",           "type": "string",
          "pipeline": [ { "op": "concat", "separator": " ",
                          "values": [ { "field": "first_name" }, { "field": "last_name" } ] } ] },
        { "name": "email",          "from": "email",          "type": "string"  },
        { "name": "lifetime_value", "from": "lifetime_value", "type": "decimal" }
      ],
      "children": [
        {
          "source": "orders", "as": "orders", "cardinality": "many",
          "on": { "parent": "customer_id", "child": "customer_id" },
          "fields": [
            { "name": "order_id",  "from": "order_id",  "type": "integer" },
            { "name": "net_total", "from": "subtotal",  "type": "decimal",
              "pipeline": [ { "op": "subtract", "rhs": { "field": "discount" } } ] }
          ],
          "children": [
            {
              "source": "line_items", "as": "items", "cardinality": "many",
              "on": { "parent": "order_id", "child": "order_id" },
              "fields": [
                { "name": "sku",      "from": "sku",        "type": "string"  },
                { "name": "quantity", "from": "quantity",   "type": "integer" },
                // line total = quantity * unit_price
                { "name": "line_total", "from": "quantity", "type": "decimal",
                  "pipeline": [ { "op": "toDecimal" }, { "op": "multiply", "rhs": { "field": "unit_price" } } ] }
              ]
            }
          ]
        }
      ],
      "load": { "fileBaseName": "customer_orders", "splitEvery": 500000 }
    }
  ]
}
```

Result for customer `1001` (pretty-printed for readability — actual output is one line per record):
```json
{
  "customer_id": 1001,
  "name": "Ada Lovelace",
  "email": "ada@example.com",
  "lifetime_value": 2540.50,
  "orders": [
    { "order_id": 9001, "net_total": 90.00, "items": [
        { "sku": "WIDGET-A", "quantity": 2, "line_total": 60.00 },
        { "sku": "WIDGET-B", "quantity": 1, "line_total": 40.00 } ] },
    { "order_id": 9002, "net_total": 50.00, "items": [
        { "sku": "WIDGET-A", "quantity": 5, "line_total": 50.00 } ] }
  ]
}
```

> Note: `name` has no `from` — it is built purely from a `concat` of two other columns. `line_total`
> starts from `quantity`, converts it to decimal, then multiplies by the `unit_price` operand.

### Example C — keyedMap with dedup

Build, per customer, a map keyed by `email::<address>`, keeping only the most recent record per address.

```jsonc
{
  "version": 1,
  "sources": {
    "customers": { "file": "customers.csv",
                   "fields": { "customer_id": { "type": "integer" }, "signup_date": { "type": "date", "format": "yyyy-MM-dd" } } }
  },
  "outputs": [
    {
      "name": "emails_by_customer", "mode": "keyedMap", "source": "customers",
      "groupBy": "customer_id",
      "keyExpr": { "pipeline": [ { "op": "concat", "values": [ { "literal": "email::" }, { "field": "email" } ] } ] },
      "dedup":   { "within": "email", "keepMaxOf": "signup_date" },
      "fields": [
        { "name": "value",       "from": "email",       "type": "string" },
        { "name": "customer_id", "from": "customer_id", "type": "integer" },
        { "name": "first_name",  "from": "first_name",  "type": "string" }
      ],
      "load": { "fileBaseName": "emails_by_customer", "splitEvery": 1000000 }
    }
  ]
}
```

Result (one object per customer group):
```json
{"email::ada@example.com":{"value":"ada@example.com","customer_id":1001,"first_name":"Ada"}}
{"email::alan@example.com":{"value":"alan@example.com","customer_id":1002,"first_name":"Alan"}}
{"email::grace@example.com":{"value":"grace@example.com","customer_id":1003,"first_name":"Grace"}}
```

### Example D — object-groups, lookups, split, and a shared `as`

A richer profile: a `tags` array (split), an object-group `ids` holding an array lookup, a scalar lookup,
and a `contacts` array that **unions** email + phone rows, where each email contact wraps its fields in a
`details` object-group with a nested `loyalty` array.

Input files:
```csv
# people.csv
id,name,tags
1,Ada,vip;beta

# emails.csv
person_id,addr,brand,email_key
1,ada@example.com,ACME,EK1
1,ada@example.com,ZENITH,EK1

# phones.csv
person_id,number
1,555-0100

# loyalty.csv
email_key,tier
EK1,GOLD
```
```jsonc
{
  "version": 1,
  "sources": {
    "people":  { "file": "people.csv",  "fields": { "id": { "type": "integer" } } },
    "emails":  { "file": "emails.csv",   "fields": { "person_id": { "type": "integer" } } },
    "phones":  { "file": "phones.csv",   "fields": { "person_id": { "type": "integer" } } },
    "loyalty": { "file": "loyalty.csv" }
  },
  "outputs": [
    {
      "name": "profiles", "mode": "object", "source": "people",
      "fields": [
        { "name": "id",   "from": "id",   "type": "integer" },
        { "name": "name", "from": "name", "type": "string" },
        { "name": "tags", "from": "tags", "type": "array", "pipeline": [ { "op": "split", "separator": ";" } ] },
        { "name": "ids", "type": "object",
          "fields": [
            { "name": "email_keys", "type": "array",
              "lookup": { "source": "emails", "on": { "parent": "id", "child": "person_id" }, "column": "email_key", "distinct": true } }
          ] },
        { "name": "first_brand", "type": "string",
          "lookup": { "source": "emails", "on": { "parent": "id", "child": "person_id" }, "column": "brand", "single": true } }
      ],
      "children": [
        { "source": "emails", "as": "contacts", "cardinality": "many", "on": { "parent": "id", "child": "person_id" },
          "fields": [
            { "name": "type",  "type": "string", "pipeline": [ { "op": "concat", "values": [ { "literal": "email" } ] } ] },
            { "name": "value", "from": "addr", "type": "string" },
            { "name": "details", "type": "object",
              "fields": [ { "name": "brand", "from": "brand", "type": "string" } ],
              "children": [
                { "source": "loyalty", "as": "loyalty", "cardinality": "many",
                  "on": { "parent": "email_key", "child": "email_key" },
                  "fields": [ { "name": "tier", "from": "tier", "type": "string" } ] }
              ] }
          ] },
        { "source": "phones", "as": "contacts", "cardinality": "many", "on": { "parent": "id", "child": "person_id" },
          "fields": [
            { "name": "type",  "type": "string", "pipeline": [ { "op": "concat", "values": [ { "literal": "phone" } ] } ] },
            { "name": "value", "from": "number", "type": "string" }
          ] }
      ],
      "load": { "fileBaseName": "profiles", "splitEvery": 1000000 }
    }
  ]
}
```

Result (pretty-printed; actual output is one line):
```json
{
  "id": 1,
  "name": "Ada",
  "tags": ["vip", "beta"],
  "ids": { "email_keys": ["EK1"] },
  "first_brand": "ACME",
  "contacts": [
    { "type": "email", "value": "ada@example.com", "details": { "brand": "ACME",   "loyalty": [ { "tier": "GOLD" } ] } },
    { "type": "email", "value": "ada@example.com", "details": { "brand": "ZENITH", "loyalty": [ { "tier": "GOLD" } ] } },
    { "type": "phone", "value": "555-0100" }
  ]
}
```

> `tags` is a `split` array; `ids` is an object-group wrapping a `distinct` array lookup; `first_brand` is a
> single lookup; `contacts` unions the `emails` and `phones` relationships (shared `as`); and each email's
> `details` is an object-group with a nested `loyalty` array.

---

## Output format

- **JSONL** — one JSON document per line.
- **Null/empty omission** — a field whose final value is null or an empty string is left out of the
  object entirely (it is not written as `null`). Set `run.emitEmpty: true` to keep blank strings (emitted
  as `""`); nulls and empty arrays are always omitted.
- **Typing** — `integer`/`decimal`/`double` emit JSON numbers, `bool` emits `true`/`false`, `date`
  emits `"yyyy-MM-dd"`, `datetime` emits ISO-8601, everything else emits a JSON string.
- **File splitting** — output rolls to a new file every `splitEvery` records; a single-file run drops
  the ordinal suffix.
- **Relaxed escaping** — characters like `@` and `+` are written literally rather than `\uXXXX`.

---

## Join strategies

Set once per run via `run.joinStrategy`.

| strategy | how it works | use when |
|---|---|---|
| `hashInMemory` *(default)* | Streams the primary source row-by-row; loads child/dimension sources into memory indexed by their join key. Handles **arbitrary nesting depth**. | Children/dimensions fit comfortably in RAM. Simple and fast. |
| `externalMerge` | Disk-backed external sort + merge join of the primary and its **direct** children in one streaming pass — neither needs to fit in RAM. | Very large primary and child files. |

Join keys are compared as **ordinal strings**, so single-column, **composite** (`on.parents`/`on.children`),
and **non-numeric** keys all work under `externalMerge`.

**`externalMerge` characteristics / limits:**
- All direct children of a node must join on the **same parent key column(s)** (otherwise use
  `hashInMemory` for that output — you'll get a clear error message).
- Levels **below** the top (grandchildren and deeper) are nested using in-memory indexes (logged at run
  time), so those sources must fit in RAM — fine for small dimensions, not for huge nested facts.
- Output is **ordered by the join key** (the primary is sorted), not source order. Content is identical
  to `hashInMemory`; only ordering differs.
- Needs scratch space in the system temp dir for the sort runs.

---

## Error handling

Two levels work together:

1. **Per-step `onError`** decides what a single transform does on failure: `throw` (default), `null`
   (yield null), or `{ "default": <value> }` (substitute).
2. **Run `onError`** decides what happens when a step *throws*:

| `run.onError` | behavior |
|---|---|
| `skip` *(default)* | Drop the offending record, log it, and append the raw primary row to `{fileBaseName}_{timestamp}_rejects.csv`. The run finishes. |
| `abort` | Stop the whole run immediately with a descriptive error. |
| `nullify` | Omit only the failing field and keep the record. |

Example: parse a quantity strictly, but never let a bad value abort the run — drop those rows and keep a
record of them.

```jsonc
"run": { "onError": "skip" },
...
{ "name": "quantity", "from": "quantity", "type": "integer",
  "pipeline": [ { "op": "toInteger", "onError": "throw" } ] }
```

---

## Type reference

| `type` | CLR | JSON output | parse notes |
|---|---|---|---|
| `string` | string | string | as-is |
| `integer` | 64-bit long | number | `long.Parse`; decimals/doubles round |
| `decimal` | decimal | number | exact; good for money |
| `double` | double | number | floating point |
| `bool` | bool | `true`/`false` | accepts `true`/`false`, `1`/`0` |
| `date` | DateTime | `"yyyy-MM-dd"` | `format` on input; ISO-8601 if omitted |
| `datetime` | DateTime | ISO-8601 (`o`) | `format` on input; ISO-8601 if omitted |
| `object` | — | nested object | an object-group: its own `fields` + nested `children` |
| `array` | — | JSON array | from a `split` transform or an array lookup; omitted when empty |

All parsing uses the **invariant culture** unless a `culture` is supplied on the field/step.

---

## Extending the engine

The engine is built around small interfaces so you can grow it without touching the pipeline:

- **Transforms** — `ITransform` implementations are registered by op name in
  `TransformRegistry.CreateDefault()`. Add a new op by registering a factory:
  ```csharp
  registry.Register("upper", _ => new UpperTransform());   // class UpperTransform : ITransform
  ```
- **Sources** — implement `ISourceReader` (today: `DelimitedSourceReader`). This is where JSON/XML
  sources will plug in; the rest of the pipeline is unaffected.
- **Output** — implement `IOutputWriter` (today: `JsonlOutputWriter`) for non-JSONL targets.

---

## Troubleshooting

| message | cause / fix |
|---|---|
| `unknown source alias 'x'` | An output/relationship references a `sources` key that doesn't exist. |
| `join 'as': parent column 'c' not found in source 'a'` | `on.parent` isn't a column in the parent source. Check the header/spelling. |
| `join 'as': child column 'c' not found in source 'b'` | `on.child` isn't a column in the child source. |
| `keyedMap mode requires 'groupBy'` / `'keyExpr'` | A `keyedMap` output is missing those fields. |
| `unknown transform op 'x'` | Misspelled op, or a custom op not registered. See the [catalog](#transform-pipeline). |
| `externalMerge requires all direct children … to join on the same parent column` | Switch that output to `hashInMemory`, or restructure so direct children share a parent key. |
| values land as strings when you expected numbers | Declare the column's input `type` under the source's `fields`, or add a `to*` step. |
| a field is missing from the output | Its value was null/empty (omitted by design), or its column name/`from` is wrong. |
| `lookup column 'c' not found in source 's'` | The lookup's `column` or `on.child` isn't in that source; `on.parent` must be a column on the current row. |
| two relationships didn't merge into one array | They must share the **exact** `as` name (and the resulting field renders after the node's scalar fields). |
| output property order looks wrong | Order follows the `fields` array; reorder the entries (relationships always render after a node's fields). |

See [`ETL-CONFIG.md`](ETL-CONFIG.md) for a terse schema reference.
