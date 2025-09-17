# Transparency Processor

This console app loads Michigan hospital pricing transparency files (CSV or JSON) into SQL Server staging tables while archiving the processed files and emitting log files.

## Prerequisites

1. **.NET SDK 8.0** (or newer) installed on the machine that will build/run the tool.
2. **Network access to SQL Server** that exposes the `transparency` schema and the stored procedure `[Discount].[transparency].[MRF_HOSPITAL_GENERAL_PROCESSING]`.
3. **Folder containing the source files** you want to ingest. The processor looks for five-digit filenames with `.csv` or `.json` extensions (e.g. `12345.csv`).
4. **Archive folder** where processed files can be moved once ingestion completes.

## Building

```bash
# from the repo root
cd src/TransparencyProcessor
dotnet build
```

The project targets `net8.0`, so the build will produce `bin/Debug/net8.0/TransparencyProcessor.dll` by default.

## Running

You can provide configuration either through command-line switches or environment variables. Command-line options take precedence over environment variables when both are present.

| Command-line | Environment | Description |
|--------------|-------------|-------------|
| `--conn`     | `SQL_CONN`  | SQL Server connection string (required). |
| `--folder`   | `FOLDER_PATH` | Directory to scan for five-digit CSV/JSON files. Defaults to current working directory. |
| `--archive`  | `ARCHIVE_PATH` | Directory where processed files will be moved. Defaults to `<current>/archive`. |
| `--batch`    | `BATCH_SIZE` | Batch size used by `SqlBulkCopy` (default `50000`). |
| `--p`        | `PARALLELISM` | Number of files to process concurrently (default `2`). |
| `--verbose`  | `VERBOSE` (set to `true`) | Emit per-file progress messages. |

### Example

```bash
export SQL_CONN="Server=tcp:sqlserver.example.com,1433;Database=MyDb;User Id=svc_user;Password=secret;Encrypt=True;"
export FOLDER_PATH="/data/transparency/incoming"
export ARCHIVE_PATH="/data/transparency/archive"

# run in Release mode
dotnet run --project src/TransparencyProcessor --configuration Release -- --verbose
```

The app will:

1. Create the folder and archive directories if they do not exist.
2. For each qualifying file:
   - Stream parse the file (wide/tall CSV or JSON) to preserve every captured charge field.
   - Upsert the hospital metadata and bulk-copy charge records into `transparency.StandardChargeInformationStaging`.
   - Execute the `[Discount].[transparency].[MRF_HOSPITAL_GENERAL_PROCESSING]` stored procedure.
   - Move the processed file into the archive directory (appending a timestamp if a file with the same name already exists).
3. Emit `ProcessingInfo.log` and, when necessary, `ProcessingErrors.log` in the source folder.

If any file fails, details are appended to `ProcessingErrors.log` and the process continues with the remaining files. The app returns exit code `0` on success, `1` when errors were logged, and `2` if no connection string was supplied.

## Operational Tips

- Run the tool on the same machine that has network access to SQL Server for the lowest latency.
- Keep the archive directory on the same volume as the source folder to minimize file move overhead.
- Use the `PARALLELISM` setting to balance throughput vs. database contention; the default of `2` works well for IO-bound workloads.
- Rotate or compress the log files in the source folder if you run the tool frequently.
