# Markroader Text Pipeline — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Markroader/Markroader/` and `Railroader-ILSPY/Assembly-CSharp/`)
**Companions:** [Daily Reports](daily-reports.md) · [Hyperlink & EntityReference](hyperlink-entityref.md) · [Passengers & Timetable](passengers-timetable.md) · [UI vanilla](ui-vanilla.md)

`Markroader` is a **standalone .NET 4.0 assembly** (`Markroader.dll`, separately versioned alongside `Railloader.Interchange`) that ships a tiny Markdown-subset parser plus a TMP-markup renderer. The parser is a one-pass regex-driven `StringSlice` walker; the renderer wraps each element type in a TMP `<style="...">` span keyed to externally-defined TMP styles (`H1`/`H2`/`H3`/`P`/`B`/`I`/`Code`/`HR`/`Link`). Headings double as `<link="anchor:slug">` jump targets — the only place where Markroader emits clickable links by default. There is **no caching layer anywhere** — every consumer re-parses + re-renders on each `OnEnable`/`Rebuild`. There are exactly **seven Assembly-CSharp call sites**: `RailroadPanelBuilder.BuildDailyReportSection`, `ReleaseNotesTextBox`, `GuideWindow`, `CreditsMenu`, `InteractiveBookWindow.PrepareStringForDisplay`, `MarkupTextBox.Populate` (via `SetTextMarkup`), and `UIPanelBuilder.AddLabelMarkup` (via `SetTextMarkup`). `StringSlice` is **also** used as the parsing primitive by `TimetableReader` — that is the only out-of-package consumer. No part of the system is host-authoritative, replicated, or persisted: it's a pure local rendering pass.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `Markroader.Parser.Parse(string)` | `Markroader/Parser.cs:28` | Full parse: front matter + line-by-line `ParseLine`. Returns `Document` |
| `Markroader.TMPMarkupRenderer.Render(...)` (3 overloads) | `Markroader/TMPMarkupRenderer.cs:10,15,20` | Render `string` / `Document` / `List<Element>` to TMP-styled string |
| `Markroader.StringExtensions.ToTMPMarkup(this string)` | `Markroader/StringExtensions.cs:5` | One-shot `Render(Parse(s))`. Single-line consumer entry |
| `Markroader.Document` | `Markroader/Document.cs:5` | `(Headers: Dictionary<string,string>, Elements: List<Element>)` |
| `Markroader.Element` (struct) | `Markroader/Element.cs:3` | `(Type, Slice, AuxSlice)` — slices not strings, zero-copy |
| `Markroader.ElementType` (enum) | `Markroader/ElementType.cs:3` | 11 values: `H1/H2/H3/Plain/Italics/Bold/Code/Link/OutlineItem/Newline/HorizontalRule` |
| `Markroader.StringSlice` (struct) | `Markroader/StringSlice.cs:6` | `(Source, Start, End)` zero-alloc cursor + `ReadRegex` overloads (1..4 groups) |
| `UI.TMPTextMarkupExtensions.SetTextMarkup(TMP_Text, string)` | `UI/TMPTextMarkupExtensions.cs:8` | The canonical TMP-side helper. Parses + renders + assigns `text` |
| `Console.ConsoleEscape(this string)` | `Console.cs:14` | `<noparse>...</noparse>` wrap. **NOT a Markroader thing**, but routinely paired with TMP rendering. |

---

## Spine: how a Markroader document becomes TMP markup

```
RAW MARKDOWN STRING                                    (e.g. file content, KVO blob, string literal)
        │
        ▼
Parser.Parse(string)                                   ← Markroader/Parser.cs:28
  │  StringSlice cursor walks line-by-line
  │  if FrontMatterPattern matches:
  │     read `---` block, gather `key: value` headers into Document.Headers
  │  for every other line:
  │     ParseLine(line, list)                         ← Markroader/Parser.cs:62
  │       try HorizontalRuleRegex   → HR
  │       else try BulletRegex      → OutlineItem (Slice=marker, AuxSlice=indent)
  │       else try HeadlineRegex    → H1/H2/H3 (length switch on `#` count)
  │       else inline pass:
  │         try TextRegex           → Plain (coalesced with previous Plain)
  │         try LinkRegex           → Link (Slice=label, AuxSlice=address)
  │         try ItalicsRegex        → Italics
  │         try BoldRegex           → Bold
  │         try MonoRegex           → Code
  │         else: append single char to existing/new Plain (slow path)
  │     append Newline element after every line
  │
  ▼
Document = (Headers, List<Element>)
  │
  ▼
TMPMarkupRenderer.Render(elements, sb)                 ← Markroader/TMPMarkupRenderer.cs:27
  │  for each element:
  │     headers/plain/bold/italics/link → StyleSpan("X", ReplacingCharacterSequences(text))
  │     headers also wrapped in LinkAnchor → <link="anchor:{slug}">…</link>
  │     Code → StyleSpan("Code", text)            ← skips ReplacingCharacterSequences
  │     HR → StyleSpan("HR", "")                  ← empty content, divider via TMP style
  │     Newline → "" (first), "\n" (block-adjacent), " " (else)
  │     OutlineItem → <line-height>+<width>+<align>+<sprite name="Bullet"> + <indent={N}>recurse</indent>
  │
  ▼
TMP-markup string  (e.g. <link="anchor:my-heading"><style="H1">My Heading</style></link>… )
  │
  ▼
TMP_Text.text = result                                  (no rebuild caching anywhere)
```

**Three things to internalise:**

1. **The Markdown subset is fixed and small.** No tables, no images, no blockquotes, no fenced code, no setext headers, no nested emphasis, no line breaks (`\n`s collapse to spaces unless block-adjacent). What ships is what's in `ElementType` — eleven cases. The parser *cannot* be extended without patching `ParseLine`'s decision tree directly.

2. **The renderer's output is keyed to TMP **styles**, not raw tags.** Spans like `<style="H1">…</style>` resolve against the TMP project's TMP_StyleSheet asset (`H1`, `H2`, `H3`, `P`, `B`, `I`, `Code`, `HR`, `Link`). Color, size, font-weight, underline, leading — all live in the asset. **Patching the renderer doesn't re-style anything; you'd need to swap the style sheet** (or patch `StyleSpan` to emit raw tags). The ConsoleLink style used elsewhere by `Hyperlink` is a different style; the Link style here is whatever the project's TMP sheet defines.

3. **There is NO caching, NO rebuild gating, NO invalidation.** Every paint of a Markroader-rendered control re-parses the source string and re-renders. `ReleaseNotesTextBox` runs on every `OnEnable`. `RailroadPanelBuilder.BuildDailyReportSection` runs on every panel rebuild (which happens on every `_dailyReport.report` KVO change). `GuideWindow.Populate` is the **only** consumer that pre-parses once at first show and caches `TextMeshMarkup` on its private `Document` class (note: `UI.Guide.GuideWindow.Document`, distinct from `Markroader.Document`).

---

## `Markroader.Parser` — full surface

```csharp
public static class Parser {                                        // Markroader/Parser.cs:6
    private static readonly Regex HeadlineRegex        = new("^(#+)\\s+(.*)$");
    private static readonly Regex BulletRegex          = new("^(\\s*)(\\-|\\*)\\s+");
    private static readonly Regex HorizontalRuleRegex  = new("^(\\-{3,})\\s*$");
    private static readonly Regex TextRegex            = new("^([^_*\\[`]+)");
    private static readonly Regex LinkRegex            = new("^\\[([^\\]]+)\\]\\(([^\\)]+)\\)");
    private static readonly Regex ItalicsRegex         = new("^_([^_]+)_");
    private static readonly Regex BoldRegex            = new("^\\*([^\\*]+)\\*");
    private static readonly Regex MonoRegex            = new("^`([^`]+)`");
    private static readonly Regex HeaderRegex          = new("^(\\w+?):\\s*(.*)$");
    private static readonly Regex FrontMatterPattern   = new("(?s)(?m)^---\\s*$(.*?\\n.*?:.*?)^---\\s*$");

    public static Document Parse(string input);                     // :28
    private static void   ParseLine(StringSlice line, List<Element> output);  // :62
}
```

### Markup syntax actually supported

| Syntax | ElementType | Notes |
|---|---|---|
| `# heading` / `## heading` / `### heading` | `H1`/`H2`/`H3` | `#+\s+(.*)` — count of `#` = level. **More than 3 → silently treated as `H3`** (the `_ => H3` switch arm at `Parser.cs:148`) |
| `- item` or `* item` | `OutlineItem` | Leading whitespace becomes indent (see `IndentForWhitespace`). Marker (`-`/`*`) goes into `Slice`; whitespace into `AuxSlice` |
| `---` (3+ dashes alone on a line) | `HorizontalRule` | Greedy `\\-{3,}\\s*$`. **Conflicts with em-dash `---` substitution at the *renderer* level inside text spans, see "ReplacingCharacterSequences" below** — but on a line by itself, line-level matches first |
| `_italic_` | `Italics` | `^_([^_]+)_` — must be paired, no underscore inside. Snake-case identifiers in text will **partially match** if followed by another underscore on the same line |
| `*bold*` | `Bold` | `^\\*([^\\*]+)\\*` — same caveat |
| `` `code` `` | `Code` | `^`([^`]+)`` — content not character-substituted (no em-dash conversion) |
| `[label](address)` | `Link` | Slice=label, AuxSlice=address. Address goes verbatim into `<link="…">` — no escaping |
| anything else | `Plain` | `TextRegex` matches `[^_*[`]+`. Single chars that don't match an inline rule are appended one at a time (slow path, see "Gotchas") |
| Front matter: `---` block of `key: value`s at top of doc | (not an element) | Stored in `Document.Headers`. Only consumed at the *very* start of the document and only if `FrontMatterPattern.IsMatch(input)` succeeds |
| `\n` (line break in source) | `Newline` element | Inserted **after every line**. Whether it renders as `"\n"`, `" "`, or `""` is a renderer decision |

**Conspicuously absent:**
- No `**bold**` (single-asterisk only)
- No `__italic__` (single-underscore only)
- No fenced code blocks (` ```... ``` `)
- No images (`![alt](url)`)
- No tables, no blockquotes, no setext (`====` underline) headers
- No reference-style links
- No HTML pass-through *parsing* — but the renderer doesn't escape `<` / `>` inside `Plain`, so raw TMP tags **leak through** (see "Escape handling")
- No nested emphasis (`*bold _and italic_*` would break `BoldRegex` because the inner `_` is fine, but the algorithm processes them sequentially — never compounded)
- No hard-break ` \n` (markdown convention)

### Front matter

```
---
title: My Doc
author: Adam
section: Trains
---
```

Recognized iff the *whole* document matches `(?s)(?m)^---\\s*$(.*?\\n.*?:.*?)^---\\s*$`. The flag is checked once at the start (`Parser.cs:33`); if the doc lacks the front-matter pattern the `---` line behaves as a horizontal rule. On match, header keys must satisfy `HeaderRegex = ^(\\w+?):\\s*(.*)$`. The only consumer that reads `Document.Headers` is `GuideWindow` indirectly (it picks first H1/H2 as title; doesn't use headers, but the dictionary is exposed via `Headers` if needed).

### Inline coalescing (the slow path)

Inside `ParseLine`'s inline loop:

- If `TextRegex` matches, the captured run is either appended to a trailing `Plain` element (extending its `Slice.End`) or pushed as a new `Plain` element. **Coalescing is deliberate and zero-alloc** — it extends the existing slice's `End` rather than concatenating strings.
- If *no* inline rule matches AND `line.Start` didn't move during this iteration, the parser adds a single character to the trailing `Plain` (or creates a new empty-ish Plain) and `line.Start++`. This is the **per-character fallback** that kicks in for stray `_`, `*`, `[`, `` ` `` that don't form a complete pair. It works but allocates an `Element` per orphan char in the worst case.

### `ParseLine` return shape — what `Element.Slice`/`AuxSlice` mean per type

| ElementType | `Slice` is | `AuxSlice` is |
|---|---|---|
| `H1`/`H2`/`H3` | heading text (after `#+\s+`) | empty |
| `Plain` | the run of text | empty |
| `Italics`/`Bold`/`Code` | inner content (no surrounding markers) | empty |
| `Link` | label (`[…]`) | address (`(…)`) |
| `OutlineItem` | bullet marker (`-` or `*`) | leading whitespace |
| `Newline` | `StringSlice.Empty` | empty |
| `HorizontalRule` | the `---+` run | empty |

### Patch candidates (Parser)

| Method | Why patch |
|---|---|
| `Parser.Parse(string)` | Replace the entire pipeline (e.g., add a Markdown extension via a different parser, or pre-process the input). **Static method, simple Harmony postfix.** |
| `Parser.ParseLine(StringSlice, List<Element>)` | Add a directive (e.g. `> blockquote`, custom `:::admonition`) — but you'd need to extend `ElementType` or hijack an existing one. |
| `Parser.HeadlineRegex` etc. (private static readonly) | Reflection-replace to broaden patterns. The fields are `private static readonly`; rebinding requires `FieldInfo.SetValue`. |
| Wrap `Parser.Parse` with a custom `IMarkroaderParser` interface | Cleanest: ship a sibling parser that consumes the same `Document`/`Element` types and have your renderer accept either. Vanilla doesn't define such an interface — there's no abstraction to satisfy. |

### MP authority (Parser)

- None. Pure function over a string. No side effects, no replication.

### Gotchas (Parser)

- **`HeadlineRegex`'s `_ => H3` switch arm** silently degrades `####`/`#####`/`######` to H3. Markdown specs all support H4–H6. If your mod authors documents with `####`, they render with the H3 style.
- **`ItalicsRegex`/`BoldRegex` are non-greedy at the outer end but *greedy* at the inner end.** `_a_b_c_` parses as `Italics("a")` then `Plain("b")` then `Italics("c")`, not `Italics("a_b_c")`.
- **Snake-case identifiers can italicize half a sentence.** `"set _foo_bar_baz_ now"` → `Italics("foo")`, `Plain("bar")`, `Italics("baz")`. Documentation containing `__init__`, `my_var_name`, etc. will visually corrupt.
- **`---` ambiguity:** `---` on a line by itself is a `HorizontalRule`. `---` *inside* a text run is later replaced by an em-dash `—` by the renderer. So writing `Foo---Bar` becomes `Foo—Bar`. Authors who want a literal triple-hyphen must avoid all three from being adjacent (e.g. use `--‍-` with a ZWJ or use HTML entities, but Markroader doesn't decode HTML entities so… you can't, really).
- **Front-matter detection is whole-document**, not "first line is `---`". `FrontMatterPattern.IsMatch(input)` runs over the full input. Worst-case false positive: a doc that has a `---` HR somewhere followed later by another `---` HR with `key: value`-shaped lines between will be misinterpreted as having front matter. Pathological but possible.
- **Front-matter parser writes to `dictionary` only via `HeaderRegex` matches.** Lines inside the `---`/`---` block that don't match `\\w+?:\\s*` are silently dropped. There is no error or warning.
- **Bullet regex requires whitespace after the marker.** `-no-space` does NOT become a bullet (good). But `*` followed by space at start is *both* a bullet AND would normally start `Bold` parsing — bullet is checked first per `ParseLine`'s decision tree.
- **`OutlineItem` indent comes from leading whitespace count via `IndentForWhitespace`** which only recognizes exactly: `\t`, `\t\t`, `\t\t\t`, `"  "`, `"    "`, `"      "`. **Any other indentation = level 0.** Three spaces, five spaces, mixed tabs+spaces all collapse to top-level. Authors who indent with one space get no nesting.
- **`OutlineItem` recurses into elements between itself and the next `Newline`** — but the recursion calls the same `Render(...)` overload on a sub-list, which itself may emit `OutlineItem`s from nested bullets. Vanilla nesting via deeper indent works because the parser sees them as separate top-level `OutlineItem`s with bigger AuxSlice indents, not because of recursive parsing.
- **`StringSlice.Length` is `End - Start + 1`** which means an "empty" slice has `End < Start` and `Length <= 0`. `IsEmpty => Start > End`. This is a deliberate inclusive-end convention; ranges are inclusive on both ends. **Do not use the standard `[start, end)` half-open mental model.**
- **`StringSlice.Empty` is a `static field`, not a property** (`StringSlice.cs:14`). It's mutable in principle (anyone could write `StringSlice.Empty = …`). The class initializer sets it to `new StringSlice(null, 0, -1)`. Patching that field is a global hazard.
- **`ReadRegex` overloads use `Regex.Match(Source, Start, Length)`** — so the regex `^` anchors are evaluated against `Source[Start..End]`. This is correct but means `^` doesn't mean "start of line" in multiline mode — it means "start of the slice." Fine because every regex is anchored and the slice is per-line.

---

## `Markroader.TMPMarkupRenderer` — full surface

```csharp
public static class TMPMarkupRenderer {                              // Markroader/TMPMarkupRenderer.cs:8
    public  static string Render(string str);                         // :10  parse + render
    public  static string Render(Document document);                  // :15
    public  static string Render(List<Element> elements);             // :20
    private static void   Render(List<Element> elements, StringBuilder sb);  // :27 main loop
    private static string ReplacingCharacterSequences(string);        // :116 em/en dash sub
    private static bool   IsBlockElement(ElementType);                // :129 newline-context decision
    private static string LinkAnchor(string title, string text);      // :148 wraps headings in <link="anchor:slug">
    private static string TextToSlug(string text);                    // :154 lower → strip non-alnum → 45-char trim → spaces→dashes
}
```

### Output template per ElementType

| ElementType | TMP output |
|---|---|
| `H1` | `<link="anchor:{slug}"><style="H1">{text}</style></link>` |
| `H2` | `<link="anchor:{slug}"><style="H2">{text}</style></link>` |
| `H3` | `<link="anchor:{slug}"><style="H3">{text}</style></link>` |
| `Plain` | `<style="P">{text}</style>` |
| `Bold` | `<style="B">{text}</style>` |
| `Italics` | `<style="I">{text}</style>` |
| `Code` | `<style="Code">{text}</style>` (no em-dash sub) |
| `Link` | `<link="{address}"><style="Link">{label}</style></link>` |
| `HorizontalRule` | `<style="HR"></style>` |
| `Newline` | `""` (first), `"\n"` (block-adjacent or post-newline run), `" "` (otherwise) |
| `OutlineItem` | `<line-height=0%><width={N-8}><align=right><sprite name="Bullet"></width></align>\n</line-height><indent={N}>{recurse on inline children}</indent><line-height=20%>\n</line-height>` |

`{slug}` = `TextToSlug(text)` = lower-case, strip everything not `[a-z0-9 -]`, collapse whitespace runs, **trim to 45 chars**, replace remaining whitespace with `-`. Non-injective: two distinct headings can collide, in which case `<link="anchor:foo">` resolves to the first occurrence.

`{N}` = `(IndentForWhitespace(auxSlice) + 1) * 20`. `width` is `N - 8`. So bullet column widths are 20/40/60 px for indent levels 0/1/2/3+.

### TMP rich-text tags emitted

Always (per element): `<style="…">`, `<link="…">` (for headings + Markroader-`Link` elements), and the OutlineItem composite (`<line-height>`, `<width>`, `<align>`, `<indent>`, `<sprite name="Bullet">`).

**Never emitted by Markroader directly:**
- `<size>`, `<color>`, `<voffset>`, `<font>`, `<noparse>`, `<u>`, `<b>`, `<i>` (TMP's literal bold/italic — Markroader uses `<style="B">` / `<style="I">` instead)
- `<mark>`, `<sup>`, `<sub>`
- `<style=ConsoleLink>` (that's `Hyperlink.ToString()`'s template, NOT Markroader's)

**The `<noparse>` *omission* is the big surprise.** Plain text content from a Markroader source goes verbatim into the TMP string. If the source contains `<` or `>` (e.g. `"Use the <Inspector> menu"`), TMP will try to parse them as tags. Authors must escape literally, e.g. via `<` or by avoiding angle brackets. **Mods that pipe user-supplied or KVO-derived text through Markroader are responsible for sanitizing first.** The standard pattern is `Console.ConsoleEscape(this string)` (`Console.cs:14`) which wraps in `<noparse>…</noparse>` — but you cannot wrap a *Markdown-formatted* document in `<noparse>` because that would also escape the Markdown metacharacters; you must escape *individual untrusted substrings* before composing the document.

### `ReplacingCharacterSequences` (em/en dash substitution)

```csharp
private static string ReplacingCharacterSequences(string str) {       // :116
    if (str.Contains("---")) str = str.Replace("---", "—");           // em dash
    if (str.Contains("--"))  str = str.Replace("--", "–");            // en dash
    return str;
}
```

Order matters: `---` is converted *before* `--`, so `---` becomes `—` (em), not `–-` (en + literal dash). Applied to **every** element type EXCEPT `Code` and `HorizontalRule`. Even applied to heading text, link labels, bold/italic content. **Code spans preserve `--` and `---` literally** — that's the only path to a literal triple-hyphen in the rendered output.

Side note: the order also matters because `Replace("---", "—")` runs over the whole string first, so `Foo---Bar` → `Foo—Bar`, and the second replace sees no `--`. Good. But: `Foo----Bar` (4 dashes) → `Foo—-Bar` (em + dash) → no `--` left? Actually the second pass searches the *post-first-replace* string, so the `-` after `—` doesn't form `--` because it's adjacent to `B`. So 4 dashes ends as `—-`. Five dashes: `-----` → first pass replaces `---` (greedy left-to-right): `--` left. Second pass replaces `--` → `–`. Net: 5 dashes → `—–`? Actually `String.Replace` is non-greedy in the sense that it scans left-to-right and replaces the first 3 chars, leaving 2; next pass replaces those 2. So 5→`—–`. Interesting trivia, won't trip mods unless they author dash-art.

### `IsBlockElement` decision

Inside the `Newline` switch arm (`TMPMarkupRenderer.cs:62-73`):
- Skip the very first newline (`i != 0` guard).
- If next element is a block (`H1/H2/H3/OutlineItem/HorizontalRule/Newline`) OR the previous element was also a `Newline` → `sb.AppendLine()` (i.e., emit `\n`).
- Otherwise → `sb.Append(" ")`.

**Two consecutive `Newline`s emit a `\n`** because of the second branch. So `\n\n` in source becomes `<style="P">…</style>\n` (paragraph break in TMP visual output, since TMP newlines reset to next line; visual paragraph spacing comes from the `P` style's leading).

### `LinkAnchor` and `TextToSlug` (heading anchors)

Every H1/H2/H3 is wrapped in `<link="anchor:{slug}">…</link>`. Click handlers see `"anchor:my-heading"` strings.

```csharp
private static string TextToSlug(string text) {                       // :154
    string s = text.ToLower();
    s = Regex.Replace(s, "[^a-z0-9\\s-]", "");
    s = Regex.Replace(s, "\\s+", " ").Trim();
    s = s.Substring(0, Math.Min(s.Length, 45)).Trim();                // hard 45-char cap
    return Regex.Replace(s, "\\s", "-");
}
```

- **45-char hard cap.** Long heading titles collide if their first 45 lowercased-alphanumeric characters match.
- **No diacritic stripping**: "Café" → "café" → matches `[^a-z0-9\\s-]` for `é` → "caf" (the `é` is removed). Authors writing Unicode headings get truncated slugs.
- **Slug is generated *every* render** — no cache. For a 100-heading doc, that's 100 regex compilations per render (well, two pre-compiled `Regex` calls per heading; three `Regex.Replace` calls total = `Regex.Replace` is per-call new compile? Actually no — `Regex.Replace(string, string)` uses an internal cache. But the `ToLower` + `Substring` + final regex per heading is overhead).

The `GuideWindow` uses these anchors as in-document jump targets via `JumpToLinkId` (`GuideWindow.cs:213`). Its convention is `ett:{id}` link addresses (`HandleLinkClicked` at `GuideWindow.cs:202`) which strip the `ett:` prefix and look up `anchor:{id}` in pre-extracted `LinkAnchors` sets. So:

- A heading `"Coupling 101"` becomes `<link="anchor:coupling-101">…</link>`.
- A reference link `[click](ett:coupling-101)` in *another* Guide doc renders to `<link="ett:coupling-101"><style="Link">click</style></link>`.
- Clicking it routes through `GuideWindow.HandleLinkClicked` → `JumpToLinkId("coupling-101")` → finds the doc with `anchor:coupling-101` in its set and scrolls there.

**Other Markroader consumers do not use `ett:` prefix** — they handle clicks via their own `Action<string>` callbacks. The `RailroadPanelBuilder.BuildDailyReportSection` callback is `link => Debug.Log("Unhandled link clicked: " + link)`. The `CreditsMenu` callback is `delegate {}` (no-op). The `MarkupTextBox` callback is whatever the consumer wires (only `UIPanelBuilder.AddLabelMarkup` uses it; it relies on `AddTextLinkReceiverIfNeeded` substring detection).

### Patch candidates (TMPMarkupRenderer)

| Method | Why patch |
|---|---|
| `TMPMarkupRenderer.Render(List<Element>)` (public) | Replace the entire renderer (e.g., emit Discord-flavored Markdown, BBCode, plain text). Three other public overloads delegate to this internal one — patch the `(elements, StringBuilder)` private overload to catch all paths. |
| `TMPMarkupRenderer.ReplacingCharacterSequences` (private) | Disable em/en-dash substitution, or extend with smart-quote conversion. **Note: applied to most content types, but skips `Code` and `HorizontalRule`.** |
| `TMPMarkupRenderer.LinkAnchor` (private) | Change anchor-link template (e.g., wrap headings in `<u>…</u>` for hover-able headings, or omit the anchor entirely if your consumer doesn't need jump targets). |
| `TMPMarkupRenderer.TextToSlug` (private) | Increase the 45-char cap, add diacritic stripping, change slug delimiters. |
| `TMPMarkupRenderer.IsBlockElement` (private) | Change which element types force a `\n` vs `" "` after a `Newline`. |
| `StyleSpan` local function inside `Render(elements, StringBuilder)` | **Not patchable** — it's a local function (compiles to a private static `<Render>g__StyleSpan|2_1`). Reachable via reflection / patching the parent method. Easier: patch the public `Render(List<Element>)` and rebuild. |

### MP authority (TMPMarkupRenderer)

- None. Pure function. Output is a string; downstream usage may have auth (e.g., Daily Report KVO is HostOnly), but the renderer doesn't replicate.

### Gotchas (TMPMarkupRenderer)

- **`<style="…">` requires a TMP_StyleSheet asset that defines those style names.** If your mod injects a Markroader-rendered string into a TMP_Text component bound to a *different* style sheet (or no style sheet at all), the `<style>` tags render literally as text. Use `MarkupTextBox` (which is wired to a TMP_Text serialized in the prefab with the project's style sheet) or any control instantiated via `UIPanelBuilder.AddTextArea` / `AddLabelMarkup` to get the right styles.
- **`HR` style content is empty.** The horizontal rule is rendered purely by whatever the `HR` style does (presumably a horizontal-line glyph or a styled underline). If the style doesn't emit anything visual, your HR is invisible. The element is still emitted (an empty `<style="HR"></style>`) — for layout debugging, prefix-patch `Render` and log when ElementType is HR.
- **Newlines after the *first* element of an OutlineItem's recursive sub-list are skipped.** Inside the OutlineItem case (`:75-92`), the renderer slices `[i+1, nextNewlineIdx)` and recurses on that range. The outer Newline that originally followed the bullet is consumed implicitly (the loop's `i--` then outer loop's `i++` skips it). A bullet whose content spans multiple inline elements is rendered correctly; one whose content is empty (bullet-then-newline) emits an empty `<indent>` block.
- **`OutlineItem`'s `\n` inside the bullet sprite block is unconditional.** Even if the bullet has empty content, the `<line-height=20%>\n</line-height>` trailing newline runs. Multiple consecutive empty bullets stack vertically with reduced leading.
- **Heading wrap order is `<link>...<style>...content</style></link>`.** That means the link's styled span sits inside the link tag — clicks on heading text dispatch the anchor. **Headings are accidentally clickable hyperlinks.** Consumers that don't intercept `anchor:…` clicks (e.g. Daily Report's `Debug.Log`) silently log them. Confirms in `RailroadPanelBuilder.BuildDailyReportSection` link callback design.
- **`Render(string)` is a convenience that always parses fresh.** No memo. Repeated calls with the same input do redundant work. Cache the rendered output yourself if you call it in a hot loop.
- **`StringBuilder` is allocated per `Render(elements)` overload.** The recursive `Render(List<Element>, StringBuilder)` reuses the same `sb`, but the inner OutlineItem recursion calls `Render(elements.GetRange(start, count), sb)` which allocates a `List<Element>` for the range. **`OutlineItem`-heavy documents allocate one List per bullet.**
- **No cross-platform line-ending normalization beyond `StringSlice.ReadLine`'s `\r\n`/`\n` handling.** A document with `\r`-only line endings (classic Mac) is treated as one giant line.
- **Empty input (`null` or `""`) returns an empty list and an empty rendered string** — `Parser.Parse(null)` succeeds because `StringSlice` handles `null` source by setting `End = -1`. Safe to call defensively without guarding.

---

## `Markroader.StringSlice` — the parsing primitive

```csharp
public struct StringSlice {                                          // Markroader/StringSlice.cs:6
    public readonly string Source;
    public int Start, End;                                            // inclusive both ends
    public static StringSlice Empty;                                  // mutable static field (!)

    public int Length    => End - Start + 1;
    public bool IsEmpty  => Start > End;
    public StringSlice(string source, int start, int end);
    public StringSlice(string text);                                  // sets Start=0, End=text.Length-1
    public override string ToString();                                // allocates substring
    public void Advance(int count = 1);                               // Start += count
    public StringSlice ReadLine();                                    // splits on \r\n or \n
    public StringSlice Subslice(int start, int end);                  // by indices
    public bool Equals(StringSlice other);                            // by (Source, Start, End)
    public bool Equals(string other);                                 // by ToString().Equals
    public bool StartsWith(char c);
    public bool ReadWhitespace(out int indent, out StringSlice slice);
    public bool ReadRegex(Regex regex);                               // 0 captures
    public bool ReadRegex(Regex regex, out StringSlice slice);        // 1 capture group
    public bool ReadRegex(Regex regex, out StringSlice g1, out StringSlice g2);              // 2
    public bool ReadRegex(Regex regex, out StringSlice g1, out StringSlice g2, out g3);      // 3
    public bool ReadRegex(Regex regex, out g1, out g2, out g3, out g4);                      // 4
    public StringSlice ConsumeAll();                                  // returns rest, sets Start=End+1
}
```

### Out-of-package consumer: `TimetableReader`

Used as the timetable-DSL parser primitive (`Model.Ops.Timetable/TimetableReader.cs:42-211`). The pattern there:

```csharp
StringSlice s = new StringSlice(document);
while (!s.IsEmpty) {
    StringSlice line = s.ReadLine();                                   // line-by-line
    // …
    if (line.ReadRegex(CommentRegex, out var _)) continue;
    if (!line.ReadRegex(TrainLinePrefixRegex, out var g1, out var g2, out var g3, out var g4))
        throw …;
    while (!line.IsEmpty) {
        line.ReadRegex(WhitespaceRegex);
        if (!line.ReadRegex(StationCodeRegex, out var slice2)) throw …;
        // alternating regex reads to consume the whole line
    }
}
```

**Critically, `TimetableReader` uses `StringSlice` outside `Markroader`'s parsing flow** — it never instantiates `Parser`, `Document`, or `TMPMarkupRenderer`. It's purely the cursor primitive. See [Passengers & Timetable › TimetableReader](passengers-timetable.md) for the full DSL.

### Patch candidates (StringSlice)

| Method | Why patch |
|---|---|
| `StringSlice.ReadRegex(...)` | All overloads route through `Regex.Match(Source, Start, Length)`. Patching them is risky (called per-token). Don't. |
| `StringSlice.ReadLine()` | Only handles `\r\n` and `\n`. Patch to add `\r`-only support. |
| `StringSlice.ToString()` | Allocates `Source.Substring(Start, Length)` per call. If you cache slices, prefer storing the slice and deferring the `ToString` until display. |

### Gotchas (StringSlice)

- **Inclusive end (`End`).** `new StringSlice("abc")` has `Start=0, End=2, Length=3`. `Subslice(0, 0)` is one character. The classic off-by-one trap is real.
- **`Empty` is a mutable static field.** Anyone could `StringSlice.Empty = new StringSlice("hi")`. Vanilla never does, but a Harmony patch in another mod could break parsing globally. If your mod depends on `Empty.IsEmpty`, sanity-check before assuming.
- **`ReadRegex` does NOT match `^` against position 0 of `Source`** — it matches against position `Start` (via `Regex.Match(Source, Start, Length)`). For unanchored regexes this still finds the first match within the slice; for anchored (`^`) regexes it matches at `Start`. Vanilla regexes are all `^`-anchored.
- **`ReadLine`'s 2-byte `\r\n` consumption advances `Start` to `i + 2`** but the returned subslice ends at `i - 1`, so `\r` and `\n` are correctly excluded from the line content.
- **`Equals(string other)` calls `ToString()` and then `string.Equals`.** Allocates per call. Don't use in hot loops; compare prefix bytes with `StartsWith` if possible.
- **`GetHashCode` uses `HashCode.Combine(Source, Start, End)`** — same source pointer hashed by reference (string interning depending). Safe but **not** the same as hash-of-content.
- **`Subslice(start, end)` doesn't validate bounds.** You can construct an out-of-range slice; it'll explode on `ToString` / `ReadRegex`.
- **`ReadWhitespace` counts `\t` as 4** (`StringSlice.cs:118-121`) but Markroader's `IndentForWhitespace` (in renderer) maps tabs differently (`\t`=1, `\t\t`=2…). The two are out of sync because `ReadWhitespace` isn't actually called by `Parser.ParseLine` — it's a dead helper as far as Markroader is concerned. (`TimetableReader` uses `WhitespaceRegex` instead, also not `ReadWhitespace`.)

---

## Consumers — the seven Markroader call sites

### `RailroadPanelBuilder.BuildDailyReportSection` — Daily Report

```csharp
string text = shared.LatestReportMarkup;                              // RailroadPanelBuilder.cs:68
if (string.IsNullOrEmpty(text)) text = "# Daily Report\nDaily reports are compiled at 6pm.";
string text2 = TMPMarkupRenderer.Render(Parser.Parse(text));          // :73
builder.AddTextArea(text2, link => Debug.Log("Unhandled link clicked: " + link)).Width(400f);
```

- **Re-parses on every panel rebuild.** `builder.AddObserver(shared.Observe(builder.Rebuild))` triggers a rebuild on every `_dailyReport.report` KVO change (host: every report generation; client: every snapshot/sync).
- **Link callback is `Debug.Log`** — heading anchor clicks (`anchor:…`) and any user-introduced `<link>` go nowhere. Patching this callback is the cleanest way to wire daily-report hyperlinks (cross-link [Hyperlink & EntityReference](hyperlink-entityref.md)).
- See [Daily Reports › UI](daily-reports.md#ui-railroadpanelbuilderbuilddailyreportsection).

### `ReleaseNotesTextBox`

```csharp
List<Element> elements = Parser.Parse(str).Elements;                  // ReleaseNotesTextBox.cs:40
// truncate after the 16th H3:
int num = 0;
for (int i = 0; i < elements.Count; i++) {
    if (elements[i].Type == ElementType.H3) {
        num++;
        if (num == 16) { elements.RemoveRange(i, elements.Count - i); break; }
    }
}
str = TMPMarkupRenderer.Render(elements);                             // :56
text.text = str;
```

- **Reads from `ReleaseNotes-Public.md` in the working directory** (`GetReleaseNotesPath`, `:62`). Not under StreamingAssets.
- **Hard-coded 16-H3 cap.** The release notes file conventionally uses `### Version 1.2.3` per release; this displays the 15 most recent versions and truncates the rest. To extend, patch the `num == 16` constant or replace the loop.
- **Logs parse time at Debug level** (`Log.Debug("Tokenized in {dt}, {count} tokens", …)`). The only consumer that instruments Markroader perf — useful confirmation that there is no caching anywhere.
- **`OnEnable`-triggered**: re-parses every time the release-notes panel becomes visible. Cheap (the file is small) but redundant.

### `GuideWindow` — the only caching consumer

```csharp
foreach (string item in Directory.GetFiles(Path.Combine(path, section.Path), "*.md")) {
    Markroader.Document document = Parser.Parse(File.ReadAllText(item));
    string text = TMPMarkupRenderer.Render(document.Elements);
    string title = document.Elements.First(el => el.Type == ElementType.H1 || el.Type == ElementType.H2).Slice.ToString();
    Document document2 = new Document(... title, text);                // GuideWindow.cs:132
    document2.LinkAnchors = FindLinkAnchors(text);                     // post-render anchor regex extract
    _contents.Add(document2);
}
```

- **`UI.Guide.GuideWindow.Document`** is a different type from `Markroader.Document`. The Guide caches the rendered TMP markup (`TextMeshMarkup` field) and a `HashSet<string> LinkAnchors` (extracted via post-hoc regex `<link=\"(.*?)\">`).
- **Title is the first H1 *or* H2.** No fallback.
- **Pre-parsed once per scene-load** at first `Show()`. Subsequent shows reuse `_contents`. (`OnDisable` doesn't clear `_contents`.) So Markroader runs N times where N = number of `*.md` files in `StreamingAssets/Guide/<section>/`.
- **Click handler is `HandleLinkClicked`** which expects `ett:`-prefixed addresses. **Heading anchor clicks (`anchor:…`) go through the generic `Log.Warning("Unrecognized link: …")` path** — the wrong-prefix mismatch means clicking a heading inside the Guide does nothing. Bug or design choice; either way patch `HandleLinkClicked` to add an `anchor:` case if you want in-page jumps.
- **`FindLinkAnchors` is a regex extract over the *rendered* output** — it pulls out every `<link="…">` substring (heading anchors AND content links). The set is used to decide which document hosts a given target.

### `CreditsMenu`

- Hardcoded credits string (`CreditsMenu.cs:11`) — multi-line raw string containing `# Credits`, `### Section`, plain text per line.
- One `Parser.Parse` + `TMPMarkupRenderer.Render` per menu open (single Awake/Enable cycle).
- **Link callback is `delegate {}`** — heading anchor clicks no-op silently.
- The string contains `<align="center">…</align>` raw TMP tag pass-through. **Markroader's parser doesn't know about this tag** — it goes into `Plain` elements verbatim and the renderer wraps each line of credits in `<style="P">…</style>`. The `<align>` tag spans across `Plain` spans because it's a TMP tag, not a Markroader element. Worth knowing: **TMP tags can be inlined in Markroader source and they "just work" because the parser passes them through inside Plain text runs and the renderer doesn't escape them.** This is the documented(?) escape hatch for raw TMP styling.

### `InteractiveBookWindow.PrepareStringForDisplay`

```csharp
private static string PrepareStringForDisplay(string s) {              // InteractiveBookWindow.cs:327
    if (string.IsNullOrEmpty(s)) return s;
    return s.RemovingLeadingWhitespaceFromLines().ToTMPMarkup();
}
```

- Pre-strips leading whitespace common to all lines via `Helpers.StringExtensions.RemovingLeadingWhitespaceFromLines` (`Helpers/StringExtensions.cs:51`) which normalizes Lua-string indented source to flush-left.
- Then runs the standard `ToTMPMarkup` extension (Parse + Render).
- This is the *only* path through which Lua-authored "books" get Markdown formatting. See [Scripting (MoonSharp)](scripting-moonsharp.md).

### `MarkupTextBox.Populate`

```csharp
private void Populate(string str) { text.SetTextMarkup(str); }         // UI/MarkupTextBox.cs:25
```

- A simple component: `[TextArea] string content` editable in the Inspector + `[SerializeField] TMP_Text text`. On `OnEnable` and `OnValidate` (editor-only auto-refresh on field edit), it re-renders.
- Used wherever a designer wants Inspector-editable Markdown text — **scene assets**, not code-generated. Where it's actually placed in the project depends on the prefab graph (not enumerable from decompile alone).

### `UIPanelBuilder.AddLabelMarkup(string)`

```csharp
public RectTransform AddLabelMarkup(string markup) {                   // UI.Builder/UIPanelBuilder.cs:209
    TMP_Text tMP_Text = InstantiateInContainer(_assets.labelControl);
    tMP_Text.SetTextMarkup(markup);                                    // → ToTMPMarkup → Render(Parse(markup))
    AddTextLinkReceiverIfNeeded(tMP_Text, tMP_Text.text);              // adds receiver iff "<link" substring present
    return tMP_Text.GetComponent<RectTransform>();
}
```

- The general-purpose code path for any panel builder that wants a Markdown-rendered label.
- After rendering, `AddTextLinkReceiverIfNeeded` checks the *rendered* text for the literal substring `"<link"`. **Headings always trigger receiver attach** because of the `<link="anchor:…">` wrap. Any document with H1/H2/H3 ends up with a clickable label. `OnLinkClicked` is unset → defaults to `LinkDispatcher.Open` which sees `anchor:my-heading`, fails `TryParseURI` (no matching prefix), logs "Failed to parse link", and does nothing. Cosmetically harmless.

### Comparison table (consumer behavior)

| Consumer | Caches output? | Post-process before render? | Click handler | Re-render trigger |
|---|---|---|---|---|
| `RailroadPanelBuilder.BuildDailyReportSection` | No | No | `Debug.Log` | KVO observer rebuilds panel |
| `ReleaseNotesTextBox` | No | Truncates after 16th H3 | (no receiver — `text.text =` direct) | `OnEnable` |
| `GuideWindow` | Yes (per `Document`) | Extracts `LinkAnchors` regex | `HandleLinkClicked` (only `ett:` prefix) | First `Show()` per scene |
| `CreditsMenu` | No | No | `delegate {}` no-op | `BuildPanelContent` (per menu open) |
| `InteractiveBookWindow.PrepareStringForDisplay` | No (caller may) | `RemovingLeadingWhitespaceFromLines` | depends on `IPageUI` consumer | book reload / `add_text` |
| `MarkupTextBox` | No | No | (no receiver — direct `text.text =`) | `OnEnable`, `OnValidate` |
| `UIPanelBuilder.AddLabelMarkup` | No | No | `LinkDispatcher.Open` (default via `TextLinkReceiver`, no override) | per panel rebuild |

---

## `UI.TMPTextMarkupExtensions.SetTextMarkup` — the one-liner glue

```csharp
public static void SetTextMarkup(this TMP_Text text, string markup) { // UI/TMPTextMarkupExtensions.cs:8
    text.text = markup.ToTMPMarkup();
}
```

The two-call hop into Markroader. `markup.ToTMPMarkup()` is `TMPMarkupRenderer.Render(Parser.Parse(markup))` (Markroader/StringExtensions.cs:5). Anyone calling `tmp.SetTextMarkup("# Hello")` triggers a parse + render. **No null guard** — null markup throws inside `Parser.Parse` because `Source.Length` (well, `Source?.Length ?? 0`) is OK but `FrontMatterPattern.IsMatch(null)` throws `ArgumentNullException`. **Pass empty string for "no content," not null.** Actually rechecking: `Parser.Parse` calls `string.IsNullOrEmpty(input)` first via `flag = !string.IsNullOrEmpty(input) && FrontMatterPattern.IsMatch(input)`, so null is short-circuited. The subsequent `new StringSlice(input)` accepts null (`Source = text; End = (Source?.Length ?? 0) - 1`). So `null` is safe — produces an empty list, renders empty string. Confirmed.

---

## `Pluralize` extension and its quirks

Companion utility shipped alongside Markroader-text consumers because daily-report-style aggregation uses it constantly. Lives in **the `Core` assembly**, not Markroader.

```csharp
public static class PluralizeExtensions {                              // Core/PluralizeExtensions.cs:3
    public static string Pluralize(this int number, string noun)       // → "{n} {pluralized noun}"
        => $"{number} {noun.Pluralize(number)}";

    public static string Pluralize(this string word, int number) {     // :11
        string text = "s";
        string text2 = "";
        if (word.EndsWith("y") && !word.EndsWith("ay")) {
            word = word.Substring(0, word.Length - 1);                  // strip y
            text = "ies";
            text2 = "y";                                                // singular suffix
        }
        else if (word.EndsWith("x")) text = "es";
        return word + ((number != 1) ? text : text2);
    }
}
```

### Quirks

- **`waybill` → `waybillies`** because `waybill` ends in `y`-not-`ay`? **No.** `waybill` ends in `l`, not `y`. The note in [Daily Reports](daily-reports.md) was misleading — `"waybill".Pluralize(2)` actually returns `"waybills"` correctly. The bullet in `AddWheelReportSection` uses `"car".Pluralize(waybillCars)` → `"2 cars"`, never `waybillies`. **The crib's previous mention of `waybill→waybillies` was hypothetical, not actual.**
- **`delivery` → `deliveries`** correct (ends in `y`, not `ay`).
- **`day` → `days`** correct (`ay` exception works).
- **`box` → `boxes`** correct (`x` rule).
- **`fox` → `foxes`** correct.
- **`fix` → `fixes`** correct.
- But:
  - **`fish` → `fishs`** (no `sh`/`ch` rule).
  - **`bus` → `buss`** (no `s`-ending rule — should be `buses`).
  - **`person` → `persons`** (no irregulars).
  - **`mouse` → `mouses`** (no irregulars).
  - **`industry` → `industries`** correct.
  - **`monkey` → `monkies`** WRONG (`ey` exception not in rule — only `ay` is). Real English: `monkeys`. Pluralize gives `monkies`.
  - **`day` → `days`** (correct via the `ay` exception).
  - **`key` → `kies`** WRONG. Same `ey` blind spot.
  - **`boy` → `boys`** WRONG (only `ay` is excepted; `oy`, `uy`, `ey` all incorrectly pluralize as `ies`).
- **`Pluralize(0)` returns the plural form** ("0 cars"), not "0 car" — number != 1 condition.
- **`Pluralize(-1)` returns the plural form** ("-1 cars") — not "-1 car". Negative one is not special-cased.
- **No empty-string guard.** `"".Pluralize(2)` → `"s"` (ends-with-y is false, ends-with-x is false, returns `"" + "s"`).

### Patch candidates (Pluralize)

| Method | Why patch |
|---|---|
| `PluralizeExtensions.Pluralize(string, int)` | Add `ey`/`oy`/`uy` exceptions, irregulars (`person`/`people`, `child`/`children`), `s`/`sh`/`ch`/`z` endings. Drop-in replacement; static method is easy to Harmony-patch. |
| `PluralizeExtensions.Pluralize(int, string)` | Just delegates; patch the string overload instead. |

---

## Hyperlink integration with `<link>` tags

Markroader emits `<link="…">` tags in two places:

1. **Headings**: `<link="anchor:{slug}">…</link>`. Slug from `TextToSlug`, capped at 45 chars.
2. **Markdown links**: `[label](address)` → `<link="{address}">…</link>`. The address is **not escaped, not validated** — whatever the author wrote between the parens.

Both flow through TMP's link-tag system. **Click hit-testing requires a `TextLinkReceiver` on the `TMP_Text` GameObject** — see [Hyperlink & EntityReference › TextLinkReceiver](hyperlink-entityref.md#uitextlinkreceiver--the-tmp-click-hit-tester). The auto-attach logic (`UIPanelBuilder.AddTextLinkReceiverIfNeeded`) substring-checks for `"<link"` in the rendered text — Markroader output ALWAYS contains it (because of heading anchors), so any Markroader-rendered label gets a receiver attached.

**Address-scheme interaction:**

- Headings use `anchor:` — not in `EntityReference`'s known prefixes (`industry:`, `passstop:`, `car:`, `player:`, `pos:`, `tt:`, `crew:`, `help:`). So `LinkDispatcher.Open("anchor:my-heading")` falls through `TryParseURI` (success: parses to `(prefix="anchor", id="my-heading")` — but wait, `anchor` isn't in the prefix-switch table) → returns false → `Log.Warning("Failed to parse link …")`. **Heading clicks log a warning** under the default dispatch unless intercepted.
- Markdown links can use any address. Authors writing `[click](car:LV-1234)` get a working car-inspector link. `[click](http://example.com)` opens the URL via `Application.OpenURL`. `[click](ett:my-section)` works only inside `GuideWindow` (which intercepts `ett:`).

**Conspicuously: there is no `Hyperlink.To(...)` integration with Markroader.** Authors who want a `<style=ConsoleLink>` styled hyperlink inside a Markroader document must hand-write the full TMP markup inline (which works because Markroader passes unknown tags through) — or post-process the output. See "Custom directives" patch recipe below.

---

## Escape handling — what chars need escaping?

| Character | Markroader meaning | Authoring guidance |
|---|---|---|
| `_` (underscore) | Italics delimiter | Use only in pairs. Stray `_` becomes a one-char `Plain` (slow). For literal underscore in identifiers, no escape exists — use `<noparse>my_identifier</noparse>` (TMP-level) or accept the partial-italics bug |
| `*` (asterisk) | Bold delimiter | Same caveats as `_` |
| `[` (left bracket) | Link start | Same — paired only matters with `]`, `(`, `)` |
| `` ` `` (backtick) | Code-span delimiter | Pair only |
| `#` at line start (after possible whitespace? actually `^#`) | Heading | Indent the line with at least one space to suppress (the `HeadlineRegex` is `^(#+)\s+`, so `# foo` is a heading but ` # foo` is not — but **the first regex tested is `HorizontalRuleRegex`, then `BulletRegex`, then `HeadlineRegex`, then inline pass**, so a leading-space line goes to inline pass and the `#` ends up in `Plain`) |
| `-`/`*` at line start (with whitespace after) | Bullet | To suppress: add other text first, or escape via leading non-space char |
| `---` standalone line | Horizontal rule | Embed in surrounding text: `Foo---Bar` (will become em-dash in renderer) |
| `--`/`---` inside Plain/heading text | Em/en dash substitution | Use `Code` span (` `--` `) to preserve literally |
| `<` / `>` (HTML/TMP tags) | **Not escaped, passed through as-is** | TMP will try to parse them. Wrap untrusted text in `<noparse>…</noparse>` before composing the document |
| `"` (double quote) | Not significant in source, but DANGEROUS in `Link` addresses (will break `<link="…">` markup) | Avoid in URLs. Markroader does not escape |

### `Console.ConsoleEscape(this string)` — the standard sanitizer

Lives in `Console.cs:14`:
```csharp
public static string ConsoleEscape(this string str) => "<noparse>" + str + "</noparse>";
```

- **Wraps in `<noparse>`** — TMP-level, prevents tag parsing.
- Counterpart: `Helpers.StringExtensions.NoParse(this string)` (`Helpers/StringExtensions.cs:33`) — identical implementation in a different namespace. Two functions, same behavior. Pick the one that matches your `using`.
- **Does NOT escape Markroader metacharacters.** A string `"foo *bar* baz"` wrapped in `<noparse>` becomes `<noparse>foo *bar* baz</noparse>` which TMP renders as literal "foo *bar* baz" but **inside a Markroader source document the Markroader parser sees `<noparse>foo *bar* baz</noparse>` first** and processes the `*` as bold delimiters! So:
  - Wrap user content with `ConsoleEscape` ONLY when you're composing a *TMP-output* string (e.g. chat broadcasts, console lines).
  - For Markroader-input documents, you must escape Markdown metachars yourself (e.g. by escaping `*`, `_`, `[`, `` ` ``, `#`, `-` — not all of which Markroader has an escape syntax for).
- **There is no Markroader-native escape syntax.** No `\*`, no `\_`, no backslash-escape at all. The grammar simply doesn't recognize `\` as special. Authors of untrusted content must pre-process.

### `StripHtml` — the inverse helper

`Helpers.StringExtensions.StripHtml(this string)` (`Helpers/StringExtensions.cs:8`) removes everything between `<` and `>`. **Fragile**: doesn't handle nested or unclosed tags, doesn't decode entities. Useful for getting plain-text out of TMP-rendered content for things like clipboard export, but lossy.

---

## `.ConsoleEscape()` extension — exists?

**Yes** — at `Console.cs:14`. As covered above, `<noparse>` wrap. **Not part of Markroader namespace** — it's a top-level static. Often paired with Markroader-rendered content for displaying user input safely.

---

## Performance — is rendering cached? Per-rebuild?

**No caching by default.** Every consumer except `GuideWindow` re-parses on every render. The cost per render:

- **Parser**: O(N) regex matches per line, each `Regex.Match` over a slice of the source. The regexes are `static readonly` so compilation happens once at first call. Per-line allocations: one `StringSlice` per `ReadLine`, several `StringSlice` per matched group, one `Element` per parsed element.
- **Renderer**: O(N) over elements. Per element: 1–2 string allocs (`Slice.ToString()` + concatenations in `StyleSpan`/`LinkAnchor`). For headings, an additional `TextToSlug` (3 regex calls, 2 string allocs).
- **OutlineItem recursion**: per nested-bullet sub-list, a `List<Element>.GetRange` allocation.
- **The `StringBuilder`** is reused inside one `Render` call but re-allocated per `Render` invocation.

**`ReleaseNotesTextBox` is the only consumer that times the parse**: `Log.Debug("Tokenized in {dt}, {count} tokens", …)` (`:42`). For a typical 5KB release-notes file (~100 elements), parse is sub-millisecond on modern hardware.

**Implications for mods:**
- Rebuilding a panel every frame on KVO change is expensive if the source is large. The `_dailyReport.report` blob is a few KB; not a concern. A 100KB book would notice.
- Mods that emit Markroader content per-tick (e.g., live status panels) should cache the rendered string and re-render only on source change. There's no built-in invalidation hook.
- The `GuideWindow` caching pattern (compute once, store on the consumer-side `Document` class) is the model to copy.

---

## Patch points — extending Markroader

### Custom directives (e.g. `> blockquote`, `:::admonition`)

The clean recipe:
1. **Patch `Parser.ParseLine`** (Harmony prefix) to recognize your line-level marker before the standard rules. Decide on a hijacked `ElementType` (e.g. reuse `Code` for inline admonitions) or extend the enum (requires patching `TMPMarkupRenderer.Render`'s switch + `IsBlockElement` switch).
2. **Patch `TMPMarkupRenderer.Render(List<Element>, StringBuilder)`** (Harmony prefix or transpiler) to add a case for your `ElementType`. Easier: if you reused an existing enum value, no renderer patch needed — just emit different content for it.
3. **Patch `IsBlockElement`** if your new type should force a `\n`-style newline.

### Alternative renderers (e.g. plain text, BBCode, Discord Markdown)

Don't patch the existing renderer. Instead:
1. Call `Markroader.Parser.Parse(input)` to get `List<Element>`.
2. Walk the list yourself, emitting whatever target syntax. You have full access to `Slice` and `AuxSlice` via the public struct.
3. Skip the TMP renderer entirely.

This is the path for "export daily report to webhook" or similar mod features.

### Intercepting parsing (e.g. preprocess source before parse)

Two seams:
1. **Per-consumer**: wrap the source string with `myMod.Preprocess(text)` before passing to `Parser.Parse`. Vanilla consumers can be patched at their call sites (e.g., `RailroadPanelBuilder.BuildDailyReportSection` prefix).
2. **Globally**: Harmony prefix on `Parser.Parse(string)` to mutate the `input` argument. **Caveat**: affects every consumer including `GuideWindow`, `ReleaseNotesTextBox`, `CreditsMenu`, `InteractiveBookWindow`, `MarkupTextBox`, `UIPanelBuilder.AddLabelMarkup`. Test all of them.

### Adding `<noparse>` to all `Plain` content

To mirror `Hyperlink`'s safety pattern (wrap labels in `<noparse>`):
1. Patch `TMPMarkupRenderer.Render(List<Element>, StringBuilder)` to wrap the `Plain` case's content in `<noparse>...</noparse>`.
2. **Caveat**: this disables the inline-TMP-tag pass-through that `CreditsMenu` relies on (`<align="center">…</align>` would render literally). Ship the patch as opt-in or scoped to specific consumers.

### Replacing the TMP style sheet

The renderer emits `<style="H1">…</style>` etc. The actual rendering depends on the TMP_StyleSheet bound to the consumer's TMP_Text. Two paths:
1. **Project-wide**: edit/replace the TMP_StyleSheet asset (requires Unity project access, not Harmony).
2. **Per-consumer**: at runtime, swap `TMP_Text.styleSheet` to a custom one that defines the same style names (`H1`/`H2`/`H3`/`P`/`B`/`I`/`Code`/`HR`/`Link`).

### Adding heading anchors with custom prefix

Patch `TMPMarkupRenderer.LinkAnchor`:
```csharp
[HarmonyPatch(typeof(TMPMarkupRenderer), "LinkAnchor")]
static class MyAnchorPatch {
    static bool Prefix(string title, string text, ref string __result) {
        string slug = TextToSlugSomehow(title);
        __result = $"<link=\"myprefix:{slug}\">{text}</link>";
        return false;
    }
}
```
Then handle clicks via your own `Action<string> onLinkClicked` on the receiver.

---

## MP authority

**None across the entire system.** Markroader is pure local string transformation. Replication of *content* happens at the consumer layer:

- **Daily Report**: `_dailyReport.report` KVO is HostOnly; the markdown string is replicated, then rendered locally on each client. See [Daily Reports › MP authority](daily-reports.md#multiplayer-authority).
- **Release Notes**: read from local file `ReleaseNotes-Public.md`. Not replicated.
- **Guide**: read from `StreamingAssets/Guide/`. Same on every client; not replicated.
- **Credits**: hardcoded literal. Same everywhere.
- **InteractiveBookWindow**: book content is Lua-side (per-client load); see [Scripting (MoonSharp) › InteractiveBookRunner](scripting-moonsharp.md).
- **MarkupTextBox**: scene-asset string; same on every client.

If two clients see different markdown, that's a bug at the consumer/replication layer, not Markroader.

---

## Patch surface summary (one table)

| Goal | Hook | File:line |
|---|---|---|
| Add custom block directive | `Parser.ParseLine` Prefix + `TMPMarkupRenderer.Render(List<Element>, StringBuilder)` Postfix | `Markroader/Parser.cs:62`, `Markroader/TMPMarkupRenderer.cs:27` |
| Replace whole renderer | `TMPMarkupRenderer.Render(List<Element>)` Prefix returning custom string | `Markroader/TMPMarkupRenderer.cs:20` |
| Disable em/en-dash substitution | `TMPMarkupRenderer.ReplacingCharacterSequences` Prefix returning unchanged | `Markroader/TMPMarkupRenderer.cs:116` |
| Wrap all Plain text in `<noparse>` | `TMPMarkupRenderer.Render(...)` transpiler on the `Plain` case | `Markroader/TMPMarkupRenderer.cs:44-46` |
| Change heading anchor format/prefix | `TMPMarkupRenderer.LinkAnchor` Prefix | `Markroader/TMPMarkupRenderer.cs:148` |
| Increase 45-char slug cap | `TMPMarkupRenderer.TextToSlug` Prefix | `Markroader/TMPMarkupRenderer.cs:154` |
| Pre-process source per consumer | Patch consumer's call site (`RailroadPanelBuilder.BuildDailyReportSection`, etc.) | various |
| Pre-process source globally | `Parser.Parse(string)` Prefix mutating `input` | `Markroader/Parser.cs:28` |
| Cache rendered output | Wrap `ToTMPMarkup` in your own memoized helper; replace consumer call sites | `Markroader/StringExtensions.cs:5` |
| Fix `Pluralize` for `boy`/`monkey`/`bus`/`fish` | `Core.PluralizeExtensions.Pluralize(string, int)` Prefix | `Core/PluralizeExtensions.cs:11` |
| Wire daily-report hyperlinks | Replace `Debug.Log` link callback in `RailroadPanelBuilder.BuildDailyReportSection` | `UI.CompanyWindow/RailroadPanelBuilder.cs:74` |
| Add `anchor:` handler globally | `LinkDispatcher.Open(string)` Prefix to handle `anchor:` and dispatch to a default Guide-style scroller | `Helpers/LinkDispatcher.cs:20` |
| Lift `ReleaseNotesTextBox` 16-H3 cap | Field-replace or transpile the `num == 16` constant | `UI/ReleaseNotesTextBox.cs:49` |

---

## Gotchas (system-wide)

- **`<noparse>` is not in Markroader's emit set.** All `Plain`/`Bold`/`Italics`/heading/link content goes verbatim to TMP. Untrusted text in source → tag injection. Unique to Markroader; `Hyperlink.ToString()` (the other major TMP-emitter) is `<noparse>`-safe.
- **Headings are accidentally clickable.** Every H1/H2/H3 wraps in `<link="anchor:…">`. If your consumer's TMP_Text has a `TextLinkReceiver`, heading clicks fire — landing in a `Debug.Log` (Daily Report) or `LinkDispatcher.Open` ("Failed to parse link" warning) for non-Guide consumers.
- **Heading anchor slug collisions are silent.** Two H2s named "Configuration" and "Configuration Settings" both slug to "configuration" / "configuration-settings"; same prefix-cut to 45 chars but distinct. Two H2s named "Configuration Settings (Detailed Walkthrough Section A)" and "Configuration Settings (Detailed Walkthrough Section B)" both truncate to "configuration-settings-detailed-walkthrough-se" — collision.
- **`<style="…">` requires the named style to exist in the bound TMP_StyleSheet.** Mod-injected Markroader output into a TMP control with a *different* style sheet renders style tags literally.
- **`OutlineItem`'s emitted markup uses TMP `<sprite name="Bullet">`** which requires a sprite-asset entry named "Bullet" in the project's TMP sprite asset. Default vanilla project has it; mod-isolated TMP_Text controls may not.
- **Markdown links' `address` is not escaped.** A `[click](add"ress with quote)` produces `<link="add"ress with quote">…` which breaks TMP's tag parsing.
- **Em/en-dash substitution affects link labels too.** `[Click--here](url)` renders as "Click–here" (en-dash). Authors who want literal `--` in labels must use `<noparse>--</noparse>` *inside* the label (which Markroader doesn't natively support without TMP tag pass-through quirks) or wrap in `Code` (` `…` `) which preserves the dashes but applies code-style.
- **`Code` content is the only text path that bypasses `ReplacingCharacterSequences`.** Useful escape hatch for literal dashes and other content that should not be substituted. But Code style is presumably mono-spaced, which may not be desired.
- **Null markup is safe (returns empty).** Empty markup is safe. Whitespace-only markup parses to a single `Newline` element (which renders as nothing because of the first-element guard).
- **`StringSlice.Empty` mutability** — global static field, not a property. A bug in any code that writes to it breaks parsing globally. Vanilla never writes; mods shouldn't either.
- **The `<align="center">…</align>` pattern in `CreditsMenu` works** because Markroader passes unknown TMP tags through inside `Plain` runs. **This is the canonical trick for embedding raw TMP styling inside a Markroader document.** No documentation; works because Markroader doesn't validate or strip.
- **`ReleaseNotesTextBox.text` is `[SerializeField] private TMP_Text`** — the receiver is **not** added by Markroader (the consumer assigns directly to `text.text`, bypassing `UIPanelBuilder.AddTextLinkReceiverIfNeeded`). So release notes have no clickable links even if the markdown contains `[label](url)` syntax. Patch the prefab to attach `TextLinkReceiver`, or post-process the assignment.
- **`MarkupTextBox.OnValidate`** fires in the editor when the `content` field changes — the Inspector live-previews. Useful for designers; runtime irrelevant.
- **`InteractiveBookWindow.PrepareStringForDisplay` calls `RemovingLeadingWhitespaceFromLines` before render** — handles Lua's typical raw-string-with-indentation idiom. Other consumers don't; passing indented markdown to e.g. `MarkupTextBox` will render the indentation as part of the text (and may trigger bullet recognition if a `-` appears at the right column).
- **`Pluralize` is in `Core` namespace, not `Markroader`** — they often appear together because daily-report-style aggregation uses both, but they ship in different assemblies.
- **No localization layer.** Markroader has no string-table integration. All headings, labels, hardcoded report sections are English literals. Localizing Markroader-rendered content requires localizing the source strings before passing them in.
- **No memoization across `ToTMPMarkup` calls.** Every call re-parses + re-renders. For UI controls re-rendering at 60Hz this is wasted work; for one-shot screens it's negligible.

---

## Cross-references

- [Daily Reports](daily-reports.md) — `RailroadPanelBuilder.BuildDailyReportSection` is the largest production consumer (the `_dailyReport.report` blob is HostOnly Markdown), with hand-rolled Pluralize aggregation in the report sections.
- [Hyperlink & EntityReference](hyperlink-entityref.md) — explains `<link>` tag click-routing via `TextLinkReceiver` and `LinkDispatcher`. **Markroader emits `<link>` but not `<style=ConsoleLink>`** — its links use the `Link` style. Heading anchors use the `anchor:…` scheme which is unknown to `LinkDispatcher` and logs a warning by default.
- [Passengers & Timetable](passengers-timetable.md) — `TimetableReader` is the only out-of-Markroader consumer of `StringSlice`. The DSL parsing pattern is identical (regex-driven cursor walk).
- [UI vanilla](ui-vanilla.md) — context for `UIPanelBuilder`, `AddTextArea`, `AddLabelMarkup`, the `_assets.textArea` prefab, and `TextLinkReceiver` lifecycle.
- [Console Commands](console-commands.md) — `Console.ConsoleEscape` (`<noparse>` wrap) lives at the top level next to `Console.Log`; standard pairing for safely embedding user content.
- [Scripting (MoonSharp)](scripting-moonsharp.md) — `InteractiveBookWindow.PrepareStringForDisplay` is the Lua-facing Markroader entry point, reached via `IPageUI.add_text` book-side calls.
