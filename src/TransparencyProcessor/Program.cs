using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

internal static class Program
{
    // ------------------------- Tunables / defaults -------------------------
    private const int DefaultBatchSize = 50_000;
    private const int DefaultParallelism = 2;    // file-level concurrency (IO bound)
    private const int FileBufferSize = 1 << 20;  // 1 MiB
    private static readonly Regex FiveDigits = new("^\\d{5}$", RegexOptions.Compiled);

    private static int Main(string[] args)
    {
        var opts = Options.Parse(args);
        if (string.IsNullOrWhiteSpace(opts.ConnectionString))
        {
            Console.Error.WriteLine("ERROR: Connection string is required. Use --conn or env SQL_CONN.");
            return 2;
        }

        Directory.CreateDirectory(opts.FolderPath);
        Directory.CreateDirectory(opts.ArchivePath);

        var infoLog = new ConcurrentBag<string>();
        var errLog = new ConcurrentBag<string>();

        // Find 5-digit files (.csv or .json)
        var files = Directory.EnumerateFiles(opts.FolderPath)
            .Where(static p =>
            {
                var name = Path.GetFileNameWithoutExtension(p);
                var ext = Path.GetExtension(p).ToLowerInvariant();
                return FiveDigits.IsMatch(name) && (ext == ".csv" || ext == ".json");
            })
            .OrderBy(static p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
        {
            Console.WriteLine("No 5-digit .csv/.json files found. Exiting.");
            return 0;
        }

        var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, opts.Parallelism) };

        Parallel.ForEach(files, po, filePath =>
        {
            var sw = Stopwatch.StartNew();
            string facCd = Path.GetFileNameWithoutExtension(filePath);
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            LogInfo(infoLog, $"[{facCd}] START {Path.GetFileName(filePath)}");

            try
            {
                HospitalInformation baseHosp;
                IEnumerable<StandardChargeInformation> stream;

                if (ext == ".json")
                {
                    stream = EnumerateJsonCharges(filePath, out baseHosp, errLog, infoLog, opts.Verbose);
                }
                else
                {
                    stream = EnumerateCsvCharges(filePath, out baseHosp, errLog, infoLog, opts.Verbose);
                }

                InsertHospitalAndChargesStream(
                    opts.ConnectionString, facCd, baseHosp,
                    stream, opts.BatchSize, errLog, infoLog, opts.Verbose);

                TryArchive(filePath, opts.ArchivePath, errLog, infoLog);
                LogInfo(infoLog, $"[{facCd}] DONE in {sw.Elapsed:mm\\:ss}.");
            }
            catch (Exception ex)
            {
                errLog.Add($"[FATAL {Path.GetFileName(filePath)}] {ex}");
            }
        });

        // Write logs
        var infoFile = Path.Combine(opts.FolderPath, "ProcessingInfo.log");
        var errorFile = Path.Combine(opts.FolderPath, "ProcessingErrors.log");
        TryAppendAll(infoFile, infoLog.ToArray());
        if (!errLog.IsEmpty) TryAppendAll(errorFile, errLog.ToArray());

        Console.WriteLine(errLog.IsEmpty ? "All files processed successfully." : "Completed with errors. See ProcessingErrors.log.");
        return errLog.IsEmpty ? 0 : 1;
    }

    // ------------------------- Options -------------------------
    private sealed record Options
    {
        public string FolderPath { get; init; } = Environment.GetEnvironmentVariable("FOLDER_PATH") ?? Directory.GetCurrentDirectory();
        public string ArchivePath { get; init; } = Environment.GetEnvironmentVariable("ARCHIVE_PATH") ?? Path.Combine(Directory.GetCurrentDirectory(), "archive");
        public string ConnectionString { get; init; } = Environment.GetEnvironmentVariable("SQL_CONN") ?? string.Empty;
        public int BatchSize { get; init; } = ParseInt(Environment.GetEnvironmentVariable("BATCH_SIZE"), DefaultBatchSize);
        public int Parallelism { get; init; } = ParseInt(Environment.GetEnvironmentVariable("PARALLELISM"), DefaultParallelism);
        public bool Verbose { get; init; } = ParseBool(Environment.GetEnvironmentVariable("VERBOSE"), false);

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next() => (i + 1 < args.Length) ? args[++i] : string.Empty;

                switch (a)
                {
                    case "--folder": o = o with { FolderPath = Next() }; break;
                    case "--archive": o = o with { ArchivePath = Next() }; break;
                    case "--conn": o = o with { ConnectionString = Next() }; break;
                    case "--batch": o = o with { BatchSize = ParseInt(Next(), DefaultBatchSize) }; break;
                    case "--p": o = o with { Parallelism = ParseInt(Next(), DefaultParallelism) }; break;
                    case "--verbose": o = o with { Verbose = true }; break;
                }
            }
            return o;
        }
    }

    private static int ParseInt(string? s, int fallback) => int.TryParse(s, out var i) ? i : fallback;
    private static bool ParseBool(string? s, bool fallback) => bool.TryParse(s, out var b) ? b : fallback;

    // ------------------------- CSV (tall/wide) streaming -------------------------
    private static IEnumerable<StandardChargeInformation> EnumerateCsvCharges(
        string path,
        out HospitalInformation baseHosp,
        ConcurrentBag<string> errors,
        ConcurrentBag<string> infoLog,
        bool verbose)
    {
        baseHosp = new HospitalInformation();
        long row = 0;

        using var csv = CsvRowReader.Open(path);

        if (!csv.TryRead(out var fileHdr))
        {
            throw new InvalidOperationException("Missing file-level header row.");
        }

        var fileMap = CreateTopLevelHeaderMap(fileHdr);

        if (!csv.TryRead(out var fileVal))
        {
            throw new InvalidOperationException("Missing file-level values row.");
        }

        var (state, _) = ParseHeaders(fileHdr, fileVal);

        baseHosp = HospitalInformation.FromFileHeaders(fileMap, fileVal, state);

        if (!csv.TryRead(out var dataHdr))
        {
            throw new InvalidOperationException("Missing data header row.");
        }
        var dataMap = CreateHeaderMap(dataHdr);

        bool isTall = DetectTallFromHeader(dataHdr);
        if (verbose)
        {
            LogInfo(infoLog, $"[{Path.GetFileName(path)}] Format: {(isTall ? "TALL" : "WIDE")}");
        }

        if (isTall)
        {
            var idx = TallIndex.Build(dataMap);
            var rowObj = new StandardChargeInformation();

            while (csv.TryRead(out var record))
            {
                row++;
                if (TallIndex.IsMostlyEmpty(record, in idx)) continue;
                if (!FillTallRowInPlace(ref rowObj, record, in idx)) continue;

                yield return rowObj;
                if ((row & 0x7FFF) == 0 && verbose)
                {
                    LogInfo(infoLog, $"[TALL] {row:n0} rows parsed…");
                }
            }
        }
        else
        {
            var wide = WideIndex.Build(dataMap);
            var negotiated = NegotiatedIndex.BuildAll(dataHdr, dataMap);

            while (csv.TryRead(out var record))
            {
                row++;
                if (WideIndex.IsProfessional(record, in wide)) continue;
                if (WideIndex.IsMostlyEmpty(record, in wide)) continue;

                bool yielded = false;
                foreach (var def in negotiated)
                {
                    if (!def.HasAnyValue(record)) continue;
                    yielded = true;

                    yield return new StandardChargeInformation
                    {
                        Description = SafeOrNull(record, wide.Desc),
                        Code = SafeOrNull(record, wide.Code1),
                        CodeType = SafeOrNull(record, wide.Code1Type),
                        Code2 = SafeOrNull(record, wide.Code2),
                        Code2Type = SafeOrNull(record, wide.Code2Type),
                        Code3 = SafeOrNull(record, wide.Code3),
                        Code3Type = SafeOrNull(record, wide.Code3Type),
                        Code4 = SafeOrNull(record, wide.Code4),
                        Code4Type = SafeOrNull(record, wide.Code4Type),
                        Modifiers = SafeOrNull(record, wide.Modifiers),
                        Setting = SafeOrNull(record, wide.Setting),
                        DrugUnitOfMeasurement = SafeOrNull(record, wide.DrugUom),
                        DrugTypeOfMeasurement = SafeOrNull(record, wide.DrugTom),
                        StandardChargeGross = ParseDec(Safe(record, wide.Gross)),
                        StandardChargeDiscountedCash = ParseDec(Safe(record, wide.DiscCash)),
                        StandardChargeMin = ParseDec(Safe(record, wide.Min)),
                        StandardChargeMax = ParseDec(Safe(record, wide.Max)),
                        PayerName = def.Payer,
                        PlanName = def.Plan,
                        StandardChargeMethodology = SafeOrNull(record, def.Methodology),
                        StandardChargeNegotiatedDollar = ParseDec(Safe(record, def.Dollar)),
                        StandardChargeNegotiatedPercentage = ParseDec(Safe(record, def.Percentage)),
                        StandardChargeNegotiatedAlgorithm = SafeOrNull(record, def.Algorithm),
                        EstimatedAmount = ParseDec(Safe(record, def.EstAmtPlan)) ?? ParseDec(Safe(record, def.EstAmtPayer)),
                        AdditionalGenericNotes = SafeOrNull(record, def.Notes)
                    };
                }

                if (!yielded)
                {
                    yield return new StandardChargeInformation
                    {
                        Description = SafeOrNull(record, wide.Desc),
                        Code = SafeOrNull(record, wide.Code1),
                        CodeType = SafeOrNull(record, wide.Code1Type),
                        Code2 = SafeOrNull(record, wide.Code2),
                        Code2Type = SafeOrNull(record, wide.Code2Type),
                        Code3 = SafeOrNull(record, wide.Code3),
                        Code3Type = SafeOrNull(record, wide.Code3Type),
                        Code4 = SafeOrNull(record, wide.Code4),
                        Code4Type = SafeOrNull(record, wide.Code4Type),
                        Modifiers = SafeOrNull(record, wide.Modifiers),
                        Setting = SafeOrNull(record, wide.Setting),
                        DrugUnitOfMeasurement = SafeOrNull(record, wide.DrugUom),
                        DrugTypeOfMeasurement = SafeOrNull(record, wide.DrugTom),
                        StandardChargeGross = ParseDec(Safe(record, wide.Gross)),
                        StandardChargeDiscountedCash = ParseDec(Safe(record, wide.DiscCash)),
                        StandardChargeMin = ParseDec(Safe(record, wide.Min)),
                        StandardChargeMax = ParseDec(Safe(record, wide.Max))
                    };
                }

                if ((row & 0x7FFF) == 0 && verbose)
                {
                    LogInfo(infoLog, $"[WIDE] {row:n0} rows parsed…");
                }
            }
        }
    }

    private static bool DetectTallFromHeader(ReadOnlySpan<string> hdr)
    {
        for (int i = 0; i < hdr.Length; i++)
        {
            var s = (hdr[i] ?? string.Empty).Trim();
            if (s.Length == 0) continue;
            s = s.ToLowerInvariant();
            if (s == "payer_name" || s == "plan_name") return true;
            if (s.StartsWith("standard_charge|", StringComparison.Ordinal)) return false;
        }
        return true; // safer default
    }

    private static string Safe(ReadOnlySpan<string> row, int index)
    {
        if ((uint)index >= (uint)row.Length) return string.Empty;
        return row[index] ?? string.Empty;
    }

    private static string? SafeOrNull(ReadOnlySpan<string> row, int index)
    {
        if ((uint)index >= (uint)row.Length) return null;
        var value = row[index];
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string? TrimToNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim();
    }

    // ------------------------- JSON (System.Text.Json) streaming -------------------------
    private static readonly JsonSerializerOptions Stj = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    private static IEnumerable<StandardChargeInformation> EnumerateJsonCharges(
        string path,
        out HospitalInformation baseHosp,
        ConcurrentBag<string> errors,
        ConcurrentBag<string> infoLog,
        bool verbose)
    {
        baseHosp = new HospitalInformation();
        long itemCount = 0;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(FileBufferSize);
        try
        {
            var state = new JsonReaderState(new JsonReaderOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            var fsOptions = new FileStreamOptions
            {
                Access = FileAccess.Read,
                Mode = FileMode.Open,
                Share = FileShare.Read,
                Options = FileOptions.SequentialScan,
                BufferSize = FileBufferSize
            };

            using var fs = new FileStream(path, fsOptions);
            int inBuffer = 0;

            static List<string> ReadStringArray(ref Utf8JsonReader r)
            {
                var list = new List<string>(4);
                if (!r.Read() || r.TokenType != JsonTokenType.StartArray) return list;
                while (r.Read())
                {
                    if (r.TokenType == JsonTokenType.EndArray) break;
                    string? value = r.TokenType == JsonTokenType.String ? TrimToNull(r.GetString()) : TrimToNull(r.GetRawText());
                    if (value is not null)
                    {
                        list.Add(value);
                    }
                }
                return list;
            }

            while (true)
            {
                int read = fs.Read(buffer, inBuffer, buffer.Length - inBuffer);
                bool final = read == 0;
                var span = new ReadOnlySpan<byte>(buffer, 0, inBuffer + read);
                var reader = new Utf8JsonReader(span, final, state);

                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.PropertyName)
                    {
                        if (reader.ValueTextEquals("HospitalName")) { reader.Read(); baseHosp.HospitalName = TrimToNull(reader.GetString()); }
                        else if (reader.ValueTextEquals("HospitalAddress")) { baseHosp.HospitalAddress = TrimToNull(string.Join(", ", ReadStringArray(ref reader))); }
                        else if (reader.ValueTextEquals("LastUpdatedOn")) { reader.Read(); baseHosp.LastUpdatedOn = ParseDate(TrimToNull(reader.GetString())); }
                        else if (reader.ValueTextEquals("Version")) { reader.Read(); baseHosp.Version = TrimToNull(reader.GetString()); }
                        else if (reader.ValueTextEquals("HospitalLocation")) { baseHosp.HospitalLocation = TrimToNull(string.Join(", ", ReadStringArray(ref reader))); }
                        else if (reader.ValueTextEquals("Affirmation"))
                        {
                            reader.Read(); // StartObject
                            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                            {
                                if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("AffirmationText"))
                                { reader.Read(); baseHosp.Affirmation = reader.TokenType == JsonTokenType.String ? TrimToNull(reader.GetString()) : null; }
                                else reader.Skip();
                            }
                        }
                        else if (reader.ValueTextEquals("LicenseInformation"))
                        {
                            reader.Read(); // StartObject
                            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                            {
                                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                                if (reader.ValueTextEquals("LicenseNumber")) { reader.Read(); baseHosp.LicenseNumber = TrimToNull(reader.GetString()); }
                                else if (reader.ValueTextEquals("State")) { reader.Read(); baseHosp.State = TrimToNull(reader.GetString()); }
                                else reader.Skip();
                            }
                        }
                        else if (reader.ValueTextEquals("StandardChargeInformation"))
                        {
                            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
                                throw new InvalidOperationException("StandardChargeInformation must be an array.");

                            while (reader.Read())
                            {
                                if (reader.TokenType == JsonTokenType.EndArray) break;
                                if (reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); continue; }

                                var rec = JsonSerializer.Deserialize<StandardChargeInformationJson>(ref reader, Stj);
                                if (rec?.CodeInformation is null || rec.StandardCharges is null) continue;

                                foreach (var code in rec.CodeInformation)
                                foreach (var sc in rec.StandardCharges)
                                {
                                    var baseRecord = new StandardChargeInformation
                                    {
                                        Description = TrimToNull(rec.Description),
                                        Code = TrimToNull(code?.Code),
                                        CodeType = TrimToNull(code?.Type),
                                        Setting = TrimToNull(sc.Setting),
                                        DrugUnitOfMeasurement = TrimToNull(rec.DrugUnitOfMeasurement),
                                        DrugTypeOfMeasurement = TrimToNull(rec.DrugTypeOfMeasurement),
                                        StandardChargeGross = sc.GrossCharge,
                                        StandardChargeDiscountedCash = sc.DiscountedCash,
                                        StandardChargeMin = sc.Minimum,
                                        StandardChargeMax = sc.Maximum,
                                        AdditionalGenericNotes = TrimToNull(sc.AdditionalGenericNotes)
                                    };

                                    var payers = sc.PayersInformation;
                                    if (payers is null || payers.Count == 0)
                                    {
                                        itemCount++;
                                        if ((itemCount & 0xFFFF) == 0 && verbose)
                                            LogInfo(infoLog, $"[JSON] {itemCount:n0} items parsed…");
                                        yield return baseRecord;
                                    }
                                    else
                                    {
                                        foreach (var p in payers)
                                        {
                                            var record = baseRecord;
                                            record.PayerName = TrimToNull(p?.PayerName);
                                            record.PlanName = TrimToNull(p?.PlanName);
                                            record.Modifiers = TrimToNull(p?.AdditionalPayerNotes);
                                            record.StandardChargeNegotiatedDollar = p?.StandardChargeDollar;
                                            record.StandardChargeNegotiatedPercentage = p?.StandardChargePercentage;
                                            record.StandardChargeNegotiatedAlgorithm = TrimToNull(p?.StandardChargeAlgorithm);
                                            record.EstimatedAmount = p?.EstimatedAmount;
                                            record.StandardChargeMethodology = TrimToNull(p?.Methodology);
                                            itemCount++;
                                            if ((itemCount & 0xFFFF) == 0 && verbose)
                                                LogInfo(infoLog, $"[JSON] {itemCount:n0} items parsed…");
                                            yield return record;
                                        }
                                    }
                                }
                            }
                        }
                        else
                        {
                            reader.Read();
                            reader.Skip();
                        }
                    }
                }

                state = reader.CurrentState;
                int consumed = (int)reader.BytesConsumed;
                int remain = span.Length - consumed;
                if (final) break;
                if (remain > 0) Buffer.BlockCopy(buffer, consumed, buffer, 0, remain);
                inBuffer = remain;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // ------------------------- DB bulk load -------------------------
    private static void InsertHospitalAndChargesStream(
        string connString,
        string facCd,
        HospitalInformation baseHospital,
        IEnumerable<StandardChargeInformation> chargesStream,
        int batchSize,
        ConcurrentBag<string> errors,
        ConcurrentBag<string> infoLog,
        bool verbose)
    {
        try
        {
            using var conn = new SqlConnection(connString);
            conn.Open();

            using (var cmd = new SqlCommand("DELETE FROM transparency.hospitalInformation WHERE FacCd=@f;", conn))
            { cmd.Parameters.AddWithValue("@f", facCd); cmd.CommandTimeout = 600; cmd.ExecuteNonQuery(); }

            using (var cmd = new SqlCommand(@"
                INSERT INTO transparency.hospitalInformation
                (HospitalName, HospitalAddress, LastUpdatedOn, Version, HospitalLocation,
                 Affirmation, LicenseNumber, State, FacCd, IsProcessed, IsChargeLimited)
                VALUES (@HospitalName, @HospitalAddress, @LastUpdatedOn, @Version, @HospitalLocation,
                        @Affirmation, @LicenseNumber, @State, @FacCd, 0, 0);", conn))
            {
                cmd.CommandTimeout = 600;
                baseHospital.AddParameters(cmd, facCd);
                cmd.ExecuteNonQuery();
            }

            using (var cmd = new SqlCommand("DELETE FROM transparency.StandardChargeInformationStaging WHERE FacCd=@f;", conn))
            { cmd.Parameters.AddWithValue("@f", facCd); cmd.CommandTimeout = 600; cmd.ExecuteNonQuery(); }

            long sent = 0;
            using (var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.TableLock, null))
            {
                bulk.DestinationTableName = "transparency.StandardChargeInformationStaging";
                bulk.BulkCopyTimeout = 0;
                bulk.BatchSize = Math.Max(1000, batchSize);
                bulk.EnableStreaming = true;
                foreach (var c in ChargesDataReader.ColumnOrder)
                    bulk.ColumnMappings.Add(c, c);

                using var reader = new ChargesDataReader(chargesStream, facCd);
                bulk.WriteToServer(reader);
                sent = reader.RowsRead;
            }
            if (verbose) LogInfo(infoLog, $"[BulkCopy {facCd}] rows={sent:n0}");

            using (var cmd = new SqlCommand("[Discount].[transparency].[MRF_HOSPITAL_GENERAL_PROCESSING]", conn))
            {
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.CommandTimeout = 0;
                cmd.Parameters.AddWithValue("@FAC_CD", facCd);
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            errors.Add($"DB load error [{facCd}]: {ex.Message}");
        }
    }

    // ------------------------- Helpers / logging / archive -------------------------
    private static DateTime ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return DateTime.MinValue;
        var formats = new[] {
            "yyyy-MM-dd", "MM/dd/yyyy",
            "yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-ddTHH:mm:ss.fffZ", "yyyy-MM-ddTHH:mm:ss.fff"
        };
        if (DateTime.TryParseExact(s.Trim(), formats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            return dt;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt))
            return dt;
        return DateTime.MinValue;
    }

    private static decimal? ParseDec(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var span = v.AsSpan().Trim();
        if (span.Length == 0) return null;

        if (span.Equals("n/a", StringComparison.OrdinalIgnoreCase) ||
            span.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            span.Equals("null", StringComparison.OrdinalIgnoreCase) ||
            span.Equals("-", StringComparison.OrdinalIgnoreCase) ||
            span.Equals("--", StringComparison.OrdinalIgnoreCase) ||
            span.Equals("not available", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        Span<char> buffer = stackalloc char[span.Length];
        int w = 0;
        bool negative = false;
        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];
            if (c == '$' || c == ',' || c == '%' || char.IsWhiteSpace(c) || c == '\u00A0') continue;
            if (c == '(') { negative = true; continue; }
            if (c == ')') continue;
            if (c == '\u2212' || c == '\u2012' || c == '\u2013' || c == '\u2014') { buffer[w++] = '-'; continue; }
            buffer[w++] = c;
        }

        if (w == 0) return null;
        if (decimal.TryParse(buffer[..w], NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            return negative ? -d : d;
        return null;
    }

    private static Dictionary<string, int> CreateHeaderMap(ReadOnlySpan<string> headers)
    {
        var map = new Dictionary<string, int>(headers.Length, StringComparer.Ordinal);
        for (int i = 0; i < headers.Length; i++)
        {
            var key = StandardizeHeader(headers[i]);
            if (key.Length == 0) continue;
            map[key] = i;
        }
        return map;
    }

    private static Dictionary<string, int> CreateTopLevelHeaderMap(ReadOnlySpan<string> headers)
    {
        var map = new Dictionary<string, int>(headers.Length * 2, StringComparer.Ordinal);
        for (int i = 0; i < headers.Length; i++)
        {
            var normalized = StandardizeTopLevelHeader(headers[i]);
            if (normalized.Length == 0) continue;
            map[normalized] = i;
            int pipe = normalized.IndexOf('|');
            if (pipe > 0)
            {
                var prefix = normalized[..pipe];
                if (!map.ContainsKey(prefix))
                {
                    map[prefix] = i;
                }
            }
        }
        return map;
    }

    private static (string state, int idx) ParseHeaders(ReadOnlySpan<string> headers, ReadOnlySpan<string> values)
    {
        string? state = null;
        int idx = -1;

        for (int i = 0; i < headers.Length; i++)
        {
            var normalized = StandardizeTopLevelHeader(headers[i]);
            if (normalized.StartsWith("license_number|", StringComparison.Ordinal))
            {
                idx = i;
                int pipe = normalized.IndexOf('|');
                if (pipe >= 0 && pipe + 1 < normalized.Length)
                {
                    state = normalized[(pipe + 1)..];
                    if (!string.IsNullOrEmpty(state))
                    {
                        state = state.Trim().ToUpperInvariant();
                        break;
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(state))
        {
            for (int i = 0; i < headers.Length; i++)
            {
                var normalized = StandardizeTopLevelHeader(headers[i]);
                if (normalized == "state")
                {
                    idx = i;
                    if ((uint)i < (uint)values.Length)
                    {
                        state = TrimToNull(values[i])?.ToUpperInvariant();
                    }
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(state))
            throw new InvalidOperationException("license_number header missing or malformed");

        return (state!, idx);
    }

    private static string StandardizeHeader(string? h)
    {
        var normalized = NormalizeHeader(h);
        if (normalized.Length == 0) return string.Empty;

        if (normalized.StartsWith("standard_charge|", StringComparison.Ordinal))
            return normalized;

        if (normalized.StartsWith("estimated_amount|", StringComparison.Ordinal) ||
            normalized.StartsWith("additional_generic_notes|", StringComparison.Ordinal))
            return normalized;

        if (normalized.StartsWith("code", StringComparison.Ordinal))
        {
            var span = normalized.AsSpan(4);
            int offset = 0;
            while (offset < span.Length && span[offset] == '_') offset++;
            span = span[offset..];

            if (!span.IsEmpty && char.IsDigit(span[0]))
            {
                char digit = span[0];
                if (digit is >= '1' and <= '4')
                {
                    if (span.Length > 1 && char.IsDigit(span[1]))
                        return normalized;

                    var rest = span.Length > 1 ? span[1..] : ReadOnlySpan<char>.Empty;
                    offset = 0;
                    while (offset < rest.Length && rest[offset] == '_') offset++;
                    rest = rest[offset..];

                    if (rest.StartsWith("type", StringComparison.Ordinal))
                    {
                        return digit switch
                        {
                            '1' => "code|1|type",
                            '2' => "code|2|type",
                            '3' => "code|3|type",
                            '4' => "code|4|type",
                            _ => normalized
                        };
                    }

                    return digit switch
                    {
                        '1' => "code|1",
                        '2' => "code|2",
                        '3' => "code|3",
                        '4' => "code|4",
                        _ => normalized
                    };
                }
            }
        }

        return normalized;
    }

    private static string StandardizeTopLevelHeader(string? h)
    {
        if (string.IsNullOrWhiteSpace(h)) return string.Empty;
        if (h.IndexOf("cfr 180.50", StringComparison.OrdinalIgnoreCase) >= 0) return "affirmation";
        return NormalizeHeader(h);
    }

    private static string NormalizeHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return string.Empty;
        var segments = header.Split('|');
        for (int i = 0; i < segments.Length; i++)
        {
            segments[i] = NormalizeSegment(segments[i]);
        }
        return string.Join('|', segments);
    }

    private static string NormalizeSegment(string? segment)
    {
        if (string.IsNullOrEmpty(segment)) return string.Empty;
        var span = segment.AsSpan();
        Span<char> buffer = stackalloc char[span.Length];
        int w = 0;
        bool lastWasSeparator = true;

        foreach (char raw in span)
        {
            if (char.IsLetterOrDigit(raw))
            {
                if (!lastWasSeparator)
                {
                    buffer[w++] = char.ToLowerInvariant(raw);
                    lastWasSeparator = false;
                }
                else
                {
                    if (w > 0 && buffer[w - 1] != '_')
                        buffer[w++] = '_';
                    buffer[w++] = char.ToLowerInvariant(raw);
                    lastWasSeparator = false;
                }
            }
            else
            {
                lastWasSeparator = true;
            }
        }

        if (w > 0 && buffer[w - 1] == '_') w--;
        return w == 0 ? string.Empty : new string(buffer[..w]);
    }

    private static string ToDisplayName(string normalized)
    {
        if (string.IsNullOrEmpty(normalized)) return string.Empty;
        var span = normalized.Replace('|', ' ').Replace('_', ' ').AsSpan();
        Span<char> buffer = stackalloc char[span.Length];
        int w = 0;
        bool upperNext = true;

        foreach (char raw in span)
        {
            if (char.IsWhiteSpace(raw))
            {
                if (w > 0 && buffer[w - 1] != ' ')
                    buffer[w++] = ' ';
                upperNext = true;
            }
            else
            {
                buffer[w++] = upperNext ? char.ToUpperInvariant(raw) : raw;
                upperNext = false;
            }
        }

        if (w > 0 && buffer[w - 1] == ' ') w--;
        return w == 0 ? string.Empty : new string(buffer[..w]);
    }

    private static void TryArchive(string filePath, string archivePath, ConcurrentBag<string> errors, ConcurrentBag<string> info)
    {
        try
        {
            Directory.CreateDirectory(archivePath);
            string fileName = Path.GetFileName(filePath);
            string dest = Path.Combine(archivePath, fileName);
            if (File.Exists(dest))
            {
                var stamped = Path.GetFileNameWithoutExtension(fileName) + "_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmssfff", CultureInfo.InvariantCulture) + Path.GetExtension(fileName);
                dest = Path.Combine(archivePath, stamped);
            }
            File.Move(filePath, dest);
            LogInfo(info, $"Archived -> {dest}");
        }
        catch (Exception ex)
        {
            errors.Add($"Archive error [{Path.GetFileName(filePath)}]: {ex.Message}");
        }
    }

    private static void LogInfo(ConcurrentBag<string> bag, string msg)
        => bag.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {msg}");

    private static void TryAppendAll(string path, string[] lines)
    {
        try { File.AppendAllLines(path, lines); } catch { /* best-effort */ }
    }

    // ------------------------- Index helpers for CSV -------------------------
    private readonly struct TallIndex
    {
        public readonly int Desc;
        public readonly int Code1;
        public readonly int Code1Type;
        public readonly int Code2;
        public readonly int Code2Type;
        public readonly int Code3;
        public readonly int Code3Type;
        public readonly int Code4;
        public readonly int Code4Type;
        public readonly int Mods;
        public readonly int Setting;
        public readonly int DrugUom;
        public readonly int DrugTom;
        public readonly int Gross;
        public readonly int DiscCash;
        public readonly int Payer;
        public readonly int Plan;
        public readonly int NegDollar;
        public readonly int NegPct;
        public readonly int NegAlgo;
        public readonly int EstAmt;
        public readonly int Min;
        public readonly int Max;
        public readonly int Method;
        public readonly int Notes;
        public readonly int BillingClass;

        private TallIndex(Dictionary<string, int> m)
        {
            int G(string k) => m.TryGetValue(k, out var i) ? i : -1;
            Desc = G("description");
            Code1 = G("code|1"); Code1Type = G("code|1|type");
            Code2 = G("code|2"); Code2Type = G("code|2|type");
            Code3 = G("code|3"); Code3Type = G("code|3|type");
            Code4 = G("code|4"); Code4Type = G("code|4|type");
            Mods = G("modifiers");
            Setting = G("setting");
            DrugUom = G("drug_unit_of_measurement");
            DrugTom = G("drug_type_of_measurement");
            Gross = G("standard_charge|gross");
            DiscCash = G("standard_charge|discounted_cash");
            Payer = G("payer_name");
            Plan = G("plan_name");
            NegDollar = G("standard_charge|negotiated_dollar");
            NegPct = G("standard_charge|negotiated_percentage");
            NegAlgo = G("standard_charge|negotiated_algorithm");
            EstAmt = G("estimated_amount");
            Min = G("standard_charge|min");
            Max = G("standard_charge|max");
            Method = G("standard_charge|methodology");
            Notes = G("additional_generic_notes");
            BillingClass = G("billing_class");
        }

        public static TallIndex Build(Dictionary<string, int> map) => new(map);

        public static bool IsMostlyEmpty(ReadOnlySpan<string> row, in TallIndex i)
        {
            if (!string.IsNullOrWhiteSpace(Safe(row, i.Code1))) return false;
            if (!string.IsNullOrWhiteSpace(Safe(row, i.Code2))) return false;
            if (!string.IsNullOrWhiteSpace(Safe(row, i.Code3))) return false;
            if (!string.IsNullOrWhiteSpace(Safe(row, i.Code4))) return false;
            return true;
        }
    }

    private readonly struct WideIndex
    {
        public readonly int Desc;
        public readonly int Code1;
        public readonly int Code1Type;
        public readonly int Code2;
        public readonly int Code2Type;
        public readonly int Code3;
        public readonly int Code3Type;
        public readonly int Code4;
        public readonly int Code4Type;
        public readonly int Modifiers;
        public readonly int Setting;
        public readonly int DrugUom;
        public readonly int DrugTom;
        public readonly int Gross;
        public readonly int DiscCash;
        public readonly int Min;
        public readonly int Max;
        public readonly int BillingClass;

        private WideIndex(Dictionary<string, int> m)
        {
            int G(string k) => m.TryGetValue(k, out var i) ? i : -1;
            Desc = G("description");
            Code1 = G("code|1"); Code1Type = G("code|1|type");
            Code2 = G("code|2"); Code2Type = G("code|2|type");
            Code3 = G("code|3"); Code3Type = G("code|3|type");
            Code4 = G("code|4"); Code4Type = G("code|4|type");
            Modifiers = G("modifiers");
            Setting = G("setting");
            DrugUom = G("drug_unit_of_measurement");
            DrugTom = G("drug_type_of_measurement");
            Gross = G("standard_charge|gross");
            DiscCash = G("standard_charge|discounted_cash");
            Min = G("standard_charge|min");
            Max = G("standard_charge|max");
            BillingClass = G("billing_class");
        }

        public static WideIndex Build(Dictionary<string, int> map) => new(map);

        public static bool IsProfessional(ReadOnlySpan<string> row, in WideIndex w)
        {
            if (w.BillingClass < 0) return false;
            return Safe(row, w.BillingClass).IndexOf("professional", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsMostlyEmpty(ReadOnlySpan<string> row, in WideIndex w)
        {
            if (!string.IsNullOrWhiteSpace(Safe(row, w.Code1))) return false;
            if (!string.IsNullOrWhiteSpace(Safe(row, w.Code2))) return false;
            if (!string.IsNullOrWhiteSpace(Safe(row, w.Code3))) return false;
            if (!string.IsNullOrWhiteSpace(Safe(row, w.Code4))) return false;
            return true;
        }
    }

    private readonly struct NegotiatedIndex
    {
        public readonly string Payer;
        public readonly string? Plan;
        public readonly int Methodology;
        public readonly int Dollar;
        public readonly int Percentage;
        public readonly int Algorithm;
        public readonly int EstAmtPlan;
        public readonly int EstAmtPayer;
        public readonly int Notes;

        private NegotiatedIndex(
            string payer,
            string? plan,
            int methodology,
            int dollar,
            int percentage,
            int algorithm,
            int estAmtPlan,
            int estAmtPayer,
            int notes)
        {
            Payer = payer;
            Plan = plan;
            Methodology = methodology;
            Dollar = dollar;
            Percentage = percentage;
            Algorithm = algorithm;
            EstAmtPlan = estAmtPlan;
            EstAmtPayer = estAmtPayer;
            Notes = notes;
        }

        public bool HasAnyValue(ReadOnlySpan<string> row)
        {
            if (Methodology >= 0 && !string.IsNullOrEmpty(Safe(row, Methodology))) return true;
            if (Dollar >= 0 && !string.IsNullOrEmpty(Safe(row, Dollar))) return true;
            if (Percentage >= 0 && !string.IsNullOrEmpty(Safe(row, Percentage))) return true;
            if (Algorithm >= 0 && !string.IsNullOrEmpty(Safe(row, Algorithm))) return true;
            if (Notes >= 0 && !string.IsNullOrEmpty(Safe(row, Notes))) return true;
            if (EstAmtPlan >= 0 && !string.IsNullOrEmpty(Safe(row, EstAmtPlan))) return true;
            if (EstAmtPayer >= 0 && !string.IsNullOrEmpty(Safe(row, EstAmtPayer))) return true;
            return false;
        }

        public static List<NegotiatedIndex> BuildAll(ReadOnlySpan<string> headers, Dictionary<string, int> map)
        {
            static (string? payer, string? plan) ExtractDisplayNames(string? raw)
            {
                if (string.IsNullOrEmpty(raw)) return (null, null);
                var segments = raw.Split('|');
                if (segments.Length < 2) return (null, null);

                var payer = TrimToNull(segments[1]);

                if (segments.Length <= 3) return (payer, null);

                var sb = new StringBuilder();
                for (int i = 2; i < segments.Length - 1; i++)
                {
                    var part = TrimToNull(segments[i]);
                    if (part is null) continue;
                    if (sb.Length > 0) sb.Append(" | ");
                    sb.Append(part);
                }
                return (payer, sb.Length > 0 ? sb.ToString() : null);
            }

            var list = new List<NegotiatedIndex>(64);
            foreach (var kv in map)
            {
                var key = kv.Key;
                if (!key.StartsWith("standard_charge|", StringComparison.Ordinal) ||
                    !key.EndsWith("|methodology", StringComparison.Ordinal))
                {
                    continue;
                }

                var normalizedParts = key.Split('|', StringSplitOptions.RemoveEmptyEntries);
                if (normalizedParts.Length < 3) continue;

                int last = normalizedParts.Length - 1;
                if (!string.Equals(normalizedParts[last], "methodology", StringComparison.Ordinal))
                    continue;

                string payerKey = normalizedParts.Length > 1 ? normalizedParts[1] : string.Empty;
                int planSegmentCount = normalizedParts.Length - 3;
                string? planKey = planSegmentCount > 0
                    ? string.Join('|', normalizedParts, 2, planSegmentCount)
                    : null;

                string? rawHeader = kv.Value < headers.Length ? headers[kv.Value] : null;
                var (payerDisplay, planDisplay) = ExtractDisplayNames(rawHeader);
                payerDisplay ??= ToDisplayName(payerKey);
                if (planKey is not null)
                {
                    planDisplay ??= ToDisplayName(planKey);
                }

                string baseKey = planKey is null
                    ? $"standard_charge|{payerKey}"
                    : $"standard_charge|{payerKey}|{planKey}";

                int TrySuffix(string suffix)
                    => map.TryGetValue($"{baseKey}|{suffix}", out var idx) ? idx : -1;

                int methodology = kv.Value;
                int dollar = TrySuffix("negotiated_dollar");
                int percentage = TrySuffix("negotiated_percentage");
                int algorithm = TrySuffix("negotiated_algorithm");

                int estPlan = planKey is null
                    ? -1
                    : map.TryGetValue($"estimated_amount|{payerKey}|{planKey}", out var idxPlan) ? idxPlan : -1;
                int estPayer = map.TryGetValue($"estimated_amount|{payerKey}", out var idxPayer) ? idxPayer : -1;

                int notesIndex = -1;
                if (planKey is not null)
                {
                    if (map.TryGetValue($"additional_generic_notes|{payerKey}|{planKey}", out var idxNotes))
                        notesIndex = idxNotes;
                    else if (map.TryGetValue($"additional_generic_notes|{payerKey}", out idxNotes))
                        notesIndex = idxNotes;
                }
                else if (map.TryGetValue($"additional_generic_notes|{payerKey}", out var idxNotes))
                {
                    notesIndex = idxNotes;
                }

                list.Add(new NegotiatedIndex(
                    payerDisplay ?? string.Empty,
                    planDisplay,
                    methodology,
                    dollar,
                    percentage,
                    algorithm,
                    estPlan,
                    estPayer,
                    notesIndex));
            }

            return list;
        }
    }

    private static bool FillTallRowInPlace(ref StandardChargeInformation r, ReadOnlySpan<string> f, in TallIndex i)
    {
        if (i.BillingClass >= 0 && Safe(f, i.BillingClass).IndexOf("professional", StringComparison.OrdinalIgnoreCase) >= 0)
            return false;

        r.Description = SafeOrNull(f, i.Desc);
        r.Code = SafeOrNull(f, i.Code1); r.CodeType = SafeOrNull(f, i.Code1Type);
        r.Code2 = SafeOrNull(f, i.Code2); r.Code2Type = SafeOrNull(f, i.Code2Type);
        r.Code3 = SafeOrNull(f, i.Code3); r.Code3Type = SafeOrNull(f, i.Code3Type);
        r.Code4 = SafeOrNull(f, i.Code4); r.Code4Type = SafeOrNull(f, i.Code4Type);
        r.Modifiers = SafeOrNull(f, i.Mods);
        r.Setting = SafeOrNull(f, i.Setting);
        r.DrugUnitOfMeasurement = SafeOrNull(f, i.DrugUom);
        r.DrugTypeOfMeasurement = SafeOrNull(f, i.DrugTom);
        r.StandardChargeGross = ParseDec(Safe(f, i.Gross));
        r.StandardChargeDiscountedCash = ParseDec(Safe(f, i.DiscCash));
        r.PayerName = SafeOrNull(f, i.Payer);
        r.PlanName = SafeOrNull(f, i.Plan);
        r.StandardChargeNegotiatedDollar = ParseDec(Safe(f, i.NegDollar));
        r.StandardChargeNegotiatedPercentage = ParseDec(Safe(f, i.NegPct));
        r.StandardChargeNegotiatedAlgorithm = SafeOrNull(f, i.NegAlgo);
        r.EstimatedAmount = ParseDec(Safe(f, i.EstAmt));
        r.StandardChargeMin = ParseDec(Safe(f, i.Min));
        r.StandardChargeMax = ParseDec(Safe(f, i.Max));
        r.StandardChargeMethodology = SafeOrNull(f, i.Method);
        r.AdditionalGenericNotes = SafeOrNull(f, i.Notes);
        return true;
    }

    // ------------------------- Data model + IDataReader -------------------------
    private sealed class HospitalInformation
    {
        public string? HospitalName { get; set; }
        public string? HospitalAddress { get; set; }
        public DateTime LastUpdatedOn { get; set; }
        public string? Version { get; set; }
        public string? HospitalLocation { get; set; }
        public string? Affirmation { get; set; }
        public string? LicenseNumber { get; set; }
        public string? State { get; set; }

        public void AddParameters(SqlCommand cmd, string facCd)
        {
            cmd.Parameters.AddWithValue("@HospitalName", (object?)HospitalName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@HospitalAddress", (object?)HospitalAddress ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@LastUpdatedOn", LastUpdatedOn == default ? (object)DBNull.Value : LastUpdatedOn);
            cmd.Parameters.AddWithValue("@Version", (object?)Version ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@HospitalLocation", (object?)HospitalLocation ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Affirmation", (object?)Affirmation ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@LicenseNumber", (object?)LicenseNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@State", (object?)State ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@FacCd", (object?)facCd ?? DBNull.Value);
        }

        public static HospitalInformation FromFileHeaders(IDictionary<string, int> map, ReadOnlySpan<string> vals, string state)
        {
            string? Get(string key)
            {
                var normalized = StandardizeTopLevelHeader(key);
                if (!map.TryGetValue(normalized, out var index)) return null;
                if ((uint)index >= (uint)vals.Length) return null;
                return TrimToNull(vals[index]);
            }

            return new HospitalInformation
            {
                HospitalName = Get("hospital_name"),
                HospitalAddress = Get("hospital_address"),
                LastUpdatedOn = ParseDate(Get("last_updated_on")),
                Version = Get("version"),
                HospitalLocation = Get("hospital_location"),
                Affirmation = Get("affirmation"),
                LicenseNumber = Get("license_number"),
                State = TrimToNull(state) ?? Get("state")
            };
        }
    }

    private struct StandardChargeInformation
    {
        public string? Description { get; set; }
        public string? Code { get; set; }
        public string? CodeType { get; set; }
        public string? Code2 { get; set; }
        public string? Code2Type { get; set; }
        public string? Code3 { get; set; }
        public string? Code3Type { get; set; }
        public string? Code4 { get; set; }
        public string? Code4Type { get; set; }
        public string? Setting { get; set; }
        public string? Modifiers { get; set; }
        public string? DrugUnitOfMeasurement { get; set; }
        public string? DrugTypeOfMeasurement { get; set; }
        public decimal? StandardChargeGross { get; set; }
        public decimal? StandardChargeDiscountedCash { get; set; }
        public string? PayerName { get; set; }
        public string? PlanName { get; set; }
        public decimal? StandardChargeNegotiatedDollar { get; set; }
        public decimal? StandardChargeNegotiatedPercentage { get; set; }
        public string? StandardChargeNegotiatedAlgorithm { get; set; }
        public decimal? EstimatedAmount { get; set; }
        public decimal? StandardChargeMin { get; set; }
        public decimal? StandardChargeMax { get; set; }
        public string? StandardChargeMethodology { get; set; }
        public string? AdditionalGenericNotes { get; set; }
    }

    // JSON POCOs
    private sealed class StandardChargeInformationJson
    {
        public string? Description { get; set; }
        public List<CodeInformation>? CodeInformation { get; set; }
        public List<StandardCharges>? StandardCharges { get; set; }
        public string? DrugUnitOfMeasurement { get; set; }
        public string? DrugTypeOfMeasurement { get; set; }
    }

    private sealed class CodeInformation { public string? Code { get; set; } public string? Type { get; set; } }

    private sealed class StandardCharges
    {
        public decimal? Minimum { get; set; }
        public decimal? Maximum { get; set; }
        public decimal? GrossCharge { get; set; }
        public decimal? DiscountedCash { get; set; }
        public string? Setting { get; set; }
        public List<PayersInformation>? PayersInformation { get; set; }
        public string? AdditionalGenericNotes { get; set; }
    }

    private sealed class PayersInformation
    {
        public string? PayerName { get; set; }
        public string? PlanName { get; set; }
        public string? AdditionalPayerNotes { get; set; }
        public decimal? StandardChargeDollar { get; set; }
        public string? StandardChargeAlgorithm { get; set; }
        public decimal? StandardChargePercentage { get; set; }
        public decimal? EstimatedAmount { get; set; }
        public string? Methodology { get; set; }
    }

    // IDataReader streaming into SqlBulkCopy
    private sealed class ChargesDataReader : IDataReader
    {
        public static readonly string[] ColumnOrder = new[]
        {
            "FacCd","Description","Code","CodeType","Code2","Code2Type",
            "Code3","Code3Type","Code4","Code4Type","Setting","Modifiers",
            "DrugUnitOfMeasurement","DrugTypeOfMeasurement","StandardChargeGross",
            "StandardChargeDiscountedCash","PayerName","PlanName",
            "StandardChargeNegotiatedDollar","StandardChargeNegotiatedPercentage",
            "StandardChargeNegotiatedAlgorithm","EstimatedAmount",
            "StandardChargeMin","StandardChargeMax","StandardChargeMethodology",
            "AdditionalGenericNotes"
        };

        private readonly IEnumerator<StandardChargeInformation> _enumerator;
        private readonly string _facCd;
        private readonly object[] _buffer = new object[26];
        private bool _closed;

        public long RowsRead { get; private set; }

        public ChargesDataReader(IEnumerable<StandardChargeInformation> source, string facCd)
        {
            _enumerator = (source ?? throw new ArgumentNullException(nameof(source))).GetEnumerator();
            _facCd = facCd ?? string.Empty;
        }

        public bool Read()
        {
            if (_enumerator.MoveNext())
            {
                Fill(_enumerator.Current);
                RowsRead++;
                return true;
            }
            return false;
        }

        private void Fill(StandardChargeInformation c)
        {
            _buffer[0] = _facCd;
            _buffer[1] = c.Description ?? (object)DBNull.Value;
            _buffer[2] = c.Code ?? (object)DBNull.Value;
            _buffer[3] = c.CodeType ?? (object)DBNull.Value;
            _buffer[4] = c.Code2 ?? (object)DBNull.Value;
            _buffer[5] = c.Code2Type ?? (object)DBNull.Value;
            _buffer[6] = c.Code3 ?? (object)DBNull.Value;
            _buffer[7] = c.Code3Type ?? (object)DBNull.Value;
            _buffer[8] = c.Code4 ?? (object)DBNull.Value;
            _buffer[9] = c.Code4Type ?? (object)DBNull.Value;
            _buffer[10] = c.Setting ?? (object)DBNull.Value;
            _buffer[11] = c.Modifiers ?? (object)DBNull.Value;
            _buffer[12] = c.DrugUnitOfMeasurement ?? (object)DBNull.Value;
            _buffer[13] = c.DrugTypeOfMeasurement ?? (object)DBNull.Value;
            _buffer[14] = c.StandardChargeGross ?? (object)DBNull.Value;
            _buffer[15] = c.StandardChargeDiscountedCash ?? (object)DBNull.Value;
            _buffer[16] = c.PayerName ?? (object)DBNull.Value;
            _buffer[17] = c.PlanName ?? (object)DBNull.Value;
            _buffer[18] = c.StandardChargeNegotiatedDollar ?? (object)DBNull.Value;
            _buffer[19] = c.StandardChargeNegotiatedPercentage ?? (object)DBNull.Value;
            _buffer[20] = c.StandardChargeNegotiatedAlgorithm ?? (object)DBNull.Value;
            _buffer[21] = c.EstimatedAmount ?? (object)DBNull.Value;
            _buffer[22] = c.StandardChargeMin ?? (object)DBNull.Value;
            _buffer[23] = c.StandardChargeMax ?? (object)DBNull.Value;
            _buffer[24] = c.StandardChargeMethodology ?? (object)DBNull.Value;
            _buffer[25] = c.AdditionalGenericNotes ?? (object)DBNull.Value;
        }

        public int FieldCount => 26;
        public object GetValue(int i) => _buffer[i];
        public string GetName(int i) => ColumnOrder[i];
        public int GetOrdinal(string name) => Array.FindIndex(ColumnOrder, c => c.Equals(name, StringComparison.OrdinalIgnoreCase));
        public Type GetFieldType(int i) => typeof(object);
        public bool IsDBNull(int i) => _buffer[i] is null or DBNull;
        public int GetValues(object[] values) { Array.Copy(_buffer, values, _buffer.Length); return _buffer.Length; }
        public string GetDataTypeName(int i) => GetFieldType(i).Name;
        public object this[int i] => _buffer[i];
        public object this[string name] => _buffer[GetOrdinal(name)];

        public void Close() { _closed = true; _enumerator.Dispose(); }
        public DataTable? GetSchemaTable() => null;
        public bool NextResult() => false;
        public int Depth => 0;
        public bool IsClosed => _closed;
        public int RecordsAffected => -1;

        // Unused but required:
        public bool GetBoolean(int i) => Convert.ToBoolean(_buffer[i]);
        public byte GetByte(int i) => Convert.ToByte(_buffer[i]);
        public long GetBytes(int i, long fieldOffset, byte[]? buffer, int bufferoffset, int length) => throw new NotSupportedException();
        public char GetChar(int i) => Convert.ToChar(_buffer[i]);
        public long GetChars(int i, long fieldoffset, char[]? buffer, int bufferoffset, int length) => throw new NotSupportedException();
        public IDataReader GetData(int i) => throw new NotSupportedException();
        public DateTime GetDateTime(int i) => Convert.ToDateTime(_buffer[i]);
        public decimal GetDecimal(int i) => Convert.ToDecimal(_buffer[i]);
        public double GetDouble(int i) => Convert.ToDouble(_buffer[i]);
        public float GetFloat(int i) => Convert.ToSingle(_buffer[i]);
        public Guid GetGuid(int i) => (Guid)_buffer[i];
        public short GetInt16(int i) => Convert.ToInt16(_buffer[i]);
        public int GetInt32(int i) => Convert.ToInt32(_buffer[i]);
        public long GetInt64(int i) => Convert.ToInt64(_buffer[i]);
        public string GetString(int i) => _buffer[i]?.ToString() ?? string.Empty;
    }

    private sealed class CsvRowReader : IDisposable
    {
        private readonly StreamReader _reader;
        private readonly List<string> _fields = new(256);
        private readonly StringBuilder _builder = new(512);
        private bool _disposed;

        private CsvRowReader(string path)
        {
            var options = new FileStreamOptions
            {
                Access = FileAccess.Read,
                Mode = FileMode.Open,
                Share = FileShare.Read,
                BufferSize = FileBufferSize,
                Options = FileOptions.SequentialScan
            };
            var stream = new FileStream(path, options);
            _reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: FileBufferSize, leaveOpen: false);
        }

        public static CsvRowReader Open(string path) => new(path);

        public bool TryRead(out ReadOnlySpan<string> row)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CsvRowReader));

            _fields.Clear();
            var sb = _builder;
            sb.Clear();

            bool inQuotes = false;
            bool fieldHasText = false;
            bool fieldHasNonWhitespace = false;
            bool anyData = false;

            while (true)
            {
                int ch = _reader.Read();
                if (ch == -1)
                {
                    if (inQuotes)
                        throw new InvalidDataException("Unterminated quoted field in CSV.");

                    if (!anyData && sb.Length == 0 && _fields.Count == 0)
                    {
                        row = default;
                        return false;
                    }

                    AddField(sb);
                    row = CollectionsMarshal.AsSpan(_fields);
                    return true;
                }

                char c = (char)ch;
                anyData = true;

                if (c == '"')
                {
                    if (inQuotes)
                    {
                        int peek = _reader.Peek();
                        if (peek == '"')
                        {
                            _reader.Read();
                            sb.Append('"');
                            fieldHasText = true;
                            fieldHasNonWhitespace = true;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else if (!fieldHasNonWhitespace)
                    {
                        if (fieldHasText)
                        {
                            sb.Clear();
                            fieldHasText = false;
                        }
                        inQuotes = true;
                    }
                    else
                    {
                        sb.Append('"');
                        fieldHasText = true;
                        fieldHasNonWhitespace = true;
                    }
                    continue;
                }

                if (!inQuotes)
                {
                    if (c == ',')
                    {
                        AddField(sb);
                        fieldHasText = false;
                        fieldHasNonWhitespace = false;
                        continue;
                    }
                    if (c == '\r')
                    {
                        if (_reader.Peek() == '\n') _reader.Read();
                        AddField(sb);
                        row = CollectionsMarshal.AsSpan(_fields);
                        return true;
                    }
                    if (c == '\n')
                    {
                        AddField(sb);
                        row = CollectionsMarshal.AsSpan(_fields);
                        return true;
                    }
                }

                sb.Append(c);
                fieldHasText = true;
                if (!char.IsWhiteSpace(c))
                {
                    fieldHasNonWhitespace = true;
                }
            }
        }

        private void AddField(StringBuilder sb)
        {
            var value = sb.ToString();
            _fields.Add(CleanField(value));
            sb.Clear();
        }

        private static string CleanField(string value)
        {
            if (value.Length == 0) return string.Empty;
            var span = value.AsSpan().Trim();
            if (span.Length == 0) return string.Empty;
            var trimmed = new string(span);
            if (trimmed.IndexOf('\u00C3') >= 0)
            {
                trimmed = trimmed.Replace("\u00C3\u00A2\u00E2\u0082\u00AC\u00C5\u0093", "\"", StringComparison.Ordinal)
                                 .Replace("\u00C3\u00A2\u00E2\u0082\u00AC\u00C2\u009D", "\"", StringComparison.Ordinal);
            }
            return trimmed;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _reader.Dispose();
        }
    }
}

