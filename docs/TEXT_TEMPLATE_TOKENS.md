# Directory Monitor — Text output

Each text destination has its own file path, format, rotation size, retention period, and template. Output is UTF-8 without BOM. The writer appends in batches and shares the file for read access.

## Formats

- **Plain Text / Custom Template:** renders the configured template.
- **JSON Lines:** one escaped JSON object per event.
- **CSV:** one fully quoted, escaped row per event.

The preview uses sample values. **Test / Test Write** writes one sample event to the configured target.

## Tokens

| Token | Value |
|---|---|
| `{EventType}` | New, Change, Modify Properties, or Delete |
| `{EventTime}` | Local event time |
| `{EventTime:format}` | Local time using a .NET date/time format |
| `{EventTimeUtc}` | UTC event time |
| `{EntryType}` | File or Directory |
| `{Path}` | Current path |
| `{OldPath}` | Previous path for rename/move |
| `{FileSize}` | Nullable byte size |
| `{UserName}` | Windows account running Directory Monitor |
| `{MachineName}` | Windows machine name |
| `{MachineId}` | Stable installation UUID |
| `{EventId}` | Event UUID |
| `{SequenceNumber}` | Increasing per-machine sequence |
| `{IsRecovered}` | Whether reconciliation recovered the event |

Unknown tokens block saving. Daily rotation also occurs when the configured size threshold is reached. Rotated files use a timestamped suffix and expire after the configured retention days.
