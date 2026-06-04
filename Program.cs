using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

class Program
{
    private static readonly HttpClient httpClient = new HttpClient();

    private static string inputFile = "qqwry.txt";
    private static string outputFile = "output_en.txt";
    private static string locationCachePath = "location_cache.tsv";
    private static string ispCachePath = "isp_cache.tsv";
    private static string generalCachePath = "translation_cache.tsv";

    private static string deepSeekApiKey = "";
    private static string deepSeekBaseUrl = "https://api.deepseek.com";
    private static string deepSeekModel = "deepseek-v4-flash";

    private static string locationPrompt = @"你是一个专业的地理名称翻译助手。请将以下json数组中的中文地理名称翻译为英文json。
输入格式：[""国家"", ""省"", ""市"", ""区""]
输出json格式：{""translated"": [""Country"", ""Region"", ""City"", ""District""]}
要求：
1. 使用标准的英文地理名称
2. 如果某个字段为空字符串，输出也对应为空字符串
3. 只输出json对象，不要有其他内容
4. 英文翻译不要输出省、市、县、区等后缀（如 Province、City、County、District 等），只输出地名本身";

    private static string ispPrompt = @"你是一个专业的 ISP（互联网服务提供商）名称翻译助手。请将以下中文 ISP 名称翻译为英文。
要求：
1. 若未说明，优先按照中国的语境来翻译
2. 使用公认的通用翻译，例如：
   - 教育网 → CERNET
   - 科技网 → CSTNET
   - 移动 → China Mobile
   - 电信 → China Telecom
   - 联通 → China Unicom
   - 广电 → China Broadcasting Network
   - 铁通 → China Tietong
3. 对于含有额外信息的ISP名称，使用""_""作为分隔符，格式为：""ISP_类型_所有者""或""ISP_额外信息""或""ISP_子品牌或子业务""
   示例：
   - 网宿科技联通CDN节点 → China Unicom_CDN_ChinaNetCenter
   - 电信通 → Dr.Peng_Dianxintong
   - 保留地址(This_network) → Reserved Address_(This Network)
4. ""_""仅作为分隔符，确保输出结果不以""_""开头或结尾
5. 输出json格式：{""translated"": ""ISP名称""}
6. 如果输入已经是英文，直接返回原样";

    private static readonly ConcurrentDictionary<string, string> generalCache = new();
    private static readonly ConcurrentDictionary<string, string> locationCache = new();
    private static readonly ConcurrentDictionary<string, string> ispCache = new();

    private static readonly ConcurrentQueue<CacheEntry> pendingWrites = new();
    private static readonly object cacheWriteLock = new();

    private static int googleConcurrency = 10;
    private static int deepseekConcurrency = 128;
    private static SemaphoreSlim googleConcurrencySemaphore = null!;
    private static SemaphoreSlim deepseekConcurrencySemaphore = null!;

    private const int IdxCountry = 0;
    private const int IdxRegion = 1;
    private const int IdxCity = 2;
    private const int IdxDistrict = 3;
    private const int IdxOwnerDomain = 4;
    private const int IdxIspDomain = 5;

    static async Task Main(string[] args)
    {
        var tempFile = Path.GetTempFileName();
        LoadConfiguration();

        if (!File.Exists(inputFile))
        {
            Console.WriteLine($"Input file {inputFile} not found.");
            return;
        }

        LoadAllCaches();

        var cts = new CancellationTokenSource();
        var cacheWriterTask = Task.Run(() => BackgroundCacheWriter(cts.Token));

        try
        {
            Console.WriteLine("Pass 1: Collecting texts to translate...");
            var locationTexts = new HashSet<string>();
            var ispTexts = new HashSet<string>();
            var generalTexts = new HashSet<string>();
            var commentLines = new List<string>();

            long totalLines = 0, validLines = 0;

            using (var reader = new StreamReader(inputFile, Encoding.UTF8))
            await using (var writer = new StreamWriter(tempFile, false, Encoding.UTF8))
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    totalLines++;
                    if (totalLines % 100_000 == 0)
                        Console.Write($"\rScanned {totalLines} lines...");

                    if (line.StartsWith('#'))
                    {
                        commentLines.Add(line);
                        continue;
                    }

                    await writer.WriteLineAsync(line);
                    validLines++;

                    var record = ParseLine(line);
                    if (record == null) continue;

                    string locationKey = record.GetLocationKey();
                    if (!string.IsNullOrEmpty(locationKey) && !locationCache.ContainsKey(locationKey))
                        locationTexts.Add(locationKey);

                    if (!string.IsNullOrEmpty(record.IspDomain) && !ispCache.ContainsKey(record.IspDomain))
                        ispTexts.Add(record.IspDomain);

                    foreach (var field in record.GetOtherTranslatableFields())
                    {
                        if (!string.IsNullOrEmpty(field) && !generalCache.ContainsKey(field))
                            generalTexts.Add(field);
                    }
                }
            }

            Console.WriteLine($"\nScan complete. Valid lines: {validLines}");
            Console.WriteLine("Texts to translate:");
            Console.WriteLine($"  Location quads: {locationTexts.Count}");
            Console.WriteLine($"  ISP names: {ispTexts.Count}");
            Console.WriteLine($"  Other fields: {generalTexts.Count}");

            await TranslateLocationBatchWithDeepSeekAsync(locationTexts, cts.Token);
            await TranslateIspBatchWithDeepSeekAsync(ispTexts, cts.Token);
            await TranslateBatchAsync(generalTexts, generalCache, CacheType.General, "Other fields", cts.Token);

            await cts.CancelAsync();
            await cacheWriterTask;

            Console.WriteLine("Applying translations and generating output...");
            long processed = 0;

            using (var reader = new StreamReader(tempFile, Encoding.UTF8))
            await using (var writer = new StreamWriter(outputFile, false, Encoding.UTF8))
            {
                foreach (var comment in commentLines)
                    await writer.WriteLineAsync(comment);

                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    var record = ParseLine(line);
                    if (record == null)
                    {
                        await writer.WriteLineAsync(line);
                        continue;
                    }

                    var locationKey = record.GetLocationKey();
                    if (!string.IsNullOrEmpty(locationKey) && locationCache.TryGetValue(locationKey, out var transLocation))
                        ApplyLocationTranslation(record, transLocation);

                    if (!string.IsNullOrEmpty(record.IspDomain) && ispCache.TryGetValue(record.IspDomain, out var transIsp))
                        record.Parts[IdxIspDomain] = transIsp;

                    ApplyGeneralTranslations(record);

                    var newLine = $"{record.IpCidr}\t{string.Join(",", record.Parts)}";
                    await writer.WriteLineAsync(newLine);

                    processed++;
                    if (processed % 100_000 == 0)
                        Console.Write($"\rProcessed {processed} lines...");
                }
            }

            Console.WriteLine($"\nDone! Output file: {outputFile}");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    private static void LoadConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .Build();

        deepSeekApiKey = configuration["DeepSeek:ApiKey"] ?? "";
        deepSeekBaseUrl = configuration["DeepSeek:BaseUrl"] ?? "https://api.deepseek.com";
        deepSeekModel = configuration["DeepSeek:Model"] ?? "deepseek-v4-flash";

        if (string.IsNullOrEmpty(deepSeekApiKey) || deepSeekApiKey == "your-api-key-here")
            deepSeekApiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") ?? "";

        var configLocationPrompt = (configuration["Prompts:LocationPrompt"] ?? "").Replace("\\n", "\n");
        var configIspPrompt = (configuration["Prompts:IspPrompt"] ?? "").Replace("\\n", "\n");
        if (!string.IsNullOrWhiteSpace(configLocationPrompt)) locationPrompt = configLocationPrompt;
        if (!string.IsNullOrWhiteSpace(configIspPrompt)) ispPrompt = configIspPrompt;

        inputFile = configuration["Files:InputFile"] ?? "qqwry.txt";
        outputFile = configuration["Files:OutputFile"] ?? "output_en.txt";
        locationCachePath = configuration["Files:LocationCacheFile"] ?? "location_cache.tsv";
        ispCachePath = configuration["Files:IspCacheFile"] ?? "isp_cache.tsv";
        generalCachePath = configuration["Files:GeneralCacheFile"] ?? "translation_cache.tsv";

        if (int.TryParse(configuration["Concurrency:GoogleConcurrency"], out int gCon) && gCon > 0)
            googleConcurrency = gCon;
        if (int.TryParse(configuration["Concurrency:DeepSeekConcurrency"], out int dCon) && dCon > 0)
            deepseekConcurrency = dCon;

        googleConcurrencySemaphore = new SemaphoreSlim(googleConcurrency);
        deepseekConcurrencySemaphore = new SemaphoreSlim(deepseekConcurrency);

        Console.WriteLine($"Files: Input={inputFile}, Output={outputFile}");
        Console.WriteLine($"Cache: Location={locationCachePath}, ISP={ispCachePath}, General={generalCachePath}");
        if (string.IsNullOrEmpty(deepSeekApiKey))
            Console.WriteLine("Warning: DeepSeek API Key not configured. Set it in appsettings.json or environment variable DEEPSEEK_API_KEY.");
        else
            Console.WriteLine($"DeepSeek: Model={deepSeekModel}, BaseUrl={deepSeekBaseUrl}");
        Console.WriteLine($"Concurrency: Google={googleConcurrency}, DeepSeek={deepseekConcurrency}");
    }

    private static async Task TranslateLocationBatchWithDeepSeekAsync(
        HashSet<string> locationKeys,
        CancellationToken cancellationToken)
    {
        if (locationKeys.Count == 0)
        {
            Console.WriteLine("Location quads: no new texts, skipping.");
            return;
        }

        Console.WriteLine($"Translating location quads ({locationKeys.Count} items)...");
        var total = locationKeys.Count;
        var completed = 0;
        var lastReport = 0;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = deepseekConcurrency,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(locationKeys, parallelOptions, async (locationKey, ct) =>
        {
            if (locationCache.ContainsKey(locationKey))
            {
                Interlocked.Increment(ref completed);
                return;
            }

            await deepseekConcurrencySemaphore.WaitAsync(ct);
            try
            {
                var translated = await TranslateLocationWithDeepSeekAsync(locationKey);
                var translatedValue = string.Join("|", translated);
                locationCache[locationKey] = translatedValue;
                pendingWrites.Enqueue(new CacheEntry(CacheType.Location, locationKey, translatedValue));

                var current = Interlocked.Increment(ref completed);
                if (current - lastReport >= 10 || current == total)
                {
                    lastReport = current;
                    Console.Write($"\rLocation quads: {current}/{total} ({(double)current / total:P1})");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nLocation translation failed: {locationKey} - {ex.Message}");
                locationCache[locationKey] = locationKey;
                pendingWrites.Enqueue(new CacheEntry(CacheType.Location, locationKey, locationKey));
                Interlocked.Increment(ref completed);
            }
            finally
            {
                deepseekConcurrencySemaphore.Release();
            }
        });

        Console.WriteLine($"\nLocation quads translation complete.");
    }

    private static async Task TranslateIspBatchWithDeepSeekAsync(
        HashSet<string> ispNames,
        CancellationToken cancellationToken)
    {
        if (ispNames.Count == 0)
        {
            Console.WriteLine("ISP names: no new texts, skipping.");
            return;
        }

        Console.WriteLine($"Translating ISP names ({ispNames.Count} items)...");
        var total = ispNames.Count;
        var completed = 0;
        var lastReport = 0;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = deepseekConcurrency,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(ispNames, parallelOptions, async (ispName, ct) =>
        {
            if (ispCache.ContainsKey(ispName))
            {
                Interlocked.Increment(ref completed);
                return;
            }

            await deepseekConcurrencySemaphore.WaitAsync(ct);
            try
            {
                var translated = await TranslateIspWithDeepSeekAsync(ispName);
                ispCache[ispName] = translated;
                pendingWrites.Enqueue(new CacheEntry(CacheType.Isp, ispName, translated));

                var current = Interlocked.Increment(ref completed);
                if (current - lastReport >= 10 || current == total)
                {
                    lastReport = current;
                    Console.Write($"\rISP names: {current}/{total} ({(double)current / total:P1})");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nISP translation failed: {ispName} - {ex.Message}");
                ispCache[ispName] = ispName;
                pendingWrites.Enqueue(new CacheEntry(CacheType.Isp, ispName, ispName));
                Interlocked.Increment(ref completed);
            }
            finally
            {
                deepseekConcurrencySemaphore.Release();
            }
        });

        Console.WriteLine($"\nISP names translation complete.");
    }

    private static async Task TranslateBatchAsync(
        HashSet<string> textsToTranslate,
        ConcurrentDictionary<string, string> cache,
        CacheType cacheType,
        string batchName,
        CancellationToken cancellationToken)
    {
        if (textsToTranslate.Count == 0)
        {
            Console.WriteLine($"{batchName}: no new texts, skipping.");
            return;
        }

        Console.WriteLine($"Translating {batchName} ({textsToTranslate.Count} items)...");
        var total = textsToTranslate.Count;
        var completed = 0;
        var lastReport = 0;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = googleConcurrency,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(textsToTranslate, parallelOptions, async (text, ct) =>
        {
            if (cache.ContainsKey(text))
            {
                Interlocked.Increment(ref completed);
                return;
            }

            await googleConcurrencySemaphore.WaitAsync(ct);
            try
            {
                var translated = await TranslateTextWithRetryAsync(text);
                cache[text] = translated;
                pendingWrites.Enqueue(new CacheEntry(cacheType, text, translated));

                var current = Interlocked.Increment(ref completed);
                if (current - lastReport >= 10 || current == total)
                {
                    lastReport = current;
                    Console.Write($"\r{batchName}: {current}/{total} ({(double)current / total:P1})");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n{batchName} translation failed: {text} - {ex.Message}");
                cache[text] = text;
                pendingWrites.Enqueue(new CacheEntry(cacheType, text, text));
                Interlocked.Increment(ref completed);
            }
            finally
            {
                googleConcurrencySemaphore.Release();
                await Task.Delay(30, ct);
            }
        });

        Console.WriteLine($"\n{batchName} translation complete.");
    }

    private static async Task<string[]> TranslateLocationWithDeepSeekAsync(string locationKey, int maxRetries = 3)
    {
        var parts = locationKey.Split('|');
        if (parts.Length != 4)
            throw new ArgumentException($"Invalid location quad format: {locationKey}");

        var systemPrompt = locationPrompt;
        var userPrompt = JsonSerializer.Serialize(parts);

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var requestBody = new
                {
                    model = deepSeekModel,
                    messages = new[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userPrompt }
                    },
                    response_format = new { type = "json_object" },
                    thinking = new { type = "disabled" },
                    stream = false
                };

                var jsonContent = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(HttpMethod.Post, $"{deepSeekBaseUrl}/chat/completions");
                request.Content = content;
                request.Headers.Add("Authorization", $"Bearer {deepSeekApiKey}");

                var response = await httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"\nDeepSeek API error: {(int)response.StatusCode} {response.ReasonPhrase}");
                    Console.WriteLine($"Response: {errorBody}");
                    throw new Exception($"DeepSeek API error: {(int)response.StatusCode}");
                }

                var responseJson = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseJson);
                var messageContent = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString();

                if (string.IsNullOrEmpty(messageContent))
                    throw new Exception("DeepSeek returned empty content");

                using var resultDoc = JsonDocument.Parse(messageContent);
                var translatedArray = resultDoc.RootElement.GetProperty("translated");
                var result = new string[4];
                for (var i = 0; i < 4; i++)
                    result[i] = translatedArray[i].GetString() ?? "";
                return result;
            }
            catch (Exception) when (attempt < maxRetries)
            {
                await Task.Delay(1000 * attempt);
            }
            catch (Exception ex)
            {
                throw new Exception($"Location translation failed (after {maxRetries} retries): {ex.Message}", ex);
            }
        }

        return parts;
    }

    private static async Task<string> TranslateIspWithDeepSeekAsync(string ispName, int maxRetries = 3)
    {
        var systemPrompt = ispPrompt;
        var userPrompt = $"请将以下ISP名称翻译为英文json: {ispName}";

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var requestBody = new
                {
                    model = deepSeekModel,
                    messages = new[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userPrompt }
                    },
                    response_format = new { type = "json_object" },
                    thinking = new { type = "disabled" },
                    stream = false
                };

                var jsonContent = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(HttpMethod.Post, $"{deepSeekBaseUrl}/chat/completions");
                request.Content = content;
                request.Headers.Add("Authorization", $"Bearer {deepSeekApiKey}");

                var response = await httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"\nDeepSeek API error: {(int)response.StatusCode} {response.ReasonPhrase}");
                    Console.WriteLine($"Response: {errorBody}");
                    throw new Exception($"DeepSeek API error: {(int)response.StatusCode}");
                }

                var responseJson = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseJson);
                var messageContent = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString();

                if (string.IsNullOrEmpty(messageContent))
                    throw new Exception("DeepSeek returned empty content");

                using var resultDoc = JsonDocument.Parse(messageContent);
                return resultDoc.RootElement.GetProperty("translated").GetString() ?? ispName;
            }
            catch (Exception) when (attempt < maxRetries)
            {
                await Task.Delay(1000 * attempt);
            }
            catch (Exception ex)
            {
                throw new Exception($"ISP translation failed (after {maxRetries} retries): {ex.Message}", ex);
            }
        }

        return ispName;
    }

    private static void ApplyLocationTranslation(LineRecord record, string translatedLocation)
    {
        var parts = translatedLocation.Split('|');
        if (parts.Length != 4) return;
        record.Parts[IdxCountry] = parts[0];
        record.Parts[IdxRegion] = parts[1];
        record.Parts[IdxCity] = parts[2];
        record.Parts[IdxDistrict] = parts[3];
    }

    private static void ApplyGeneralTranslations(LineRecord record)
    {
        if (!string.IsNullOrEmpty(record.District) && generalCache.TryGetValue(record.District, out var transDist))
            record.Parts[IdxDistrict] = transDist;
        if (!string.IsNullOrEmpty(record.OwnerDomain) && generalCache.TryGetValue(record.OwnerDomain, out var transOwner))
            record.Parts[IdxOwnerDomain] = transOwner;
    }

    private static LineRecord? ParseLine(string line)
    {
        var tabSplit = line.Split('\t');
        if (tabSplit.Length < 2) return null;

        var record = new LineRecord
        {
            IpCidr = tabSplit[0],
            Parts = tabSplit[1].Split(',')
        };

        if (record.Parts.Length >= 8) return record;
        var newParts = new string[8];
        Array.Copy(record.Parts, newParts, record.Parts.Length);
        for (int i = record.Parts.Length; i < 8; i++) newParts[i] = "";
        record.Parts = newParts;

        return record;
    }

    private static async Task<string> TranslateTextWithRetryAsync(string text, int maxRetries = 3)
    {
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=en&dt=t&q={Uri.EscapeDataString(text)}";
                var response = await httpClient.GetStringAsync(url);
                return ExtractTranslatedText(response);
            }
            catch (Exception) when (attempt < maxRetries)
            {
                await Task.Delay(1000 * attempt);
            }
            catch (Exception ex)
            {
                throw new Exception($"Google translation failed (after {maxRetries} retries): {ex.Message}", ex);
            }
        }
        return text;
    }

    private static string ExtractTranslatedText(string jsonResponse)
    {
        var startIdx = jsonResponse.IndexOf("[[[");
        if (startIdx == -1) throw new Exception("Invalid response format");

        var firstQuote = jsonResponse.IndexOf('"', startIdx);
        if (firstQuote == -1) throw new Exception("Translation quote not found");

        var sb = new StringBuilder();
        var escaped = false;
        var i = firstQuote + 1;
        for (; i < jsonResponse.Length; i++)
        {
            var c = jsonResponse[i];
            if (escaped)
            {
                switch (c)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u': if (i + 4 < jsonResponse.Length) i += 4; break;
                    default: sb.Append(c); break;
                }
                escaped = false;
            }
            else if (c == '\\')
            {
                escaped = true;
            }
            else if (c == '"')
            {
                break;
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static void LoadAllCaches()
    {
        LoadCacheFile(locationCachePath, locationCache, "Location quads");
        LoadCacheFile(ispCachePath, ispCache, "ISP names");
        LoadCacheFile(generalCachePath, generalCache, "Other fields");
    }

    private static void LoadCacheFile(string path, ConcurrentDictionary<string, string> dict, string name)
    {
        if (!File.Exists(path)) return;
        var count = 0;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            var parts = line.Split('\t');
            if (parts.Length != 2) continue;
            dict[parts[0]] = parts[1];
            count++;
        }
        Console.WriteLine($"Loaded {name} cache: {count} entries.");
    }

    enum CacheType { General, Location, Isp }

    record CacheEntry(CacheType Type, string Key, string Value);

    private static async Task BackgroundCacheWriter(CancellationToken ct)
    {
        const int flushIntervalMs = 2000;
        var buffer = new List<CacheEntry>();

        while (!ct.IsCancellationRequested)
        {
            while (pendingWrites.TryDequeue(out var entry))
                buffer.Add(entry);

            if (buffer.Count > 0)
            {
                try { await Task.Delay(flushIntervalMs, ct); }
                catch (TaskCanceledException) { break; }

                if (pendingWrites.IsEmpty)
                {
                    FlushBuffer(buffer);
                    buffer.Clear();
                }
            }
            else
            {
                try { await Task.Delay(flushIntervalMs, ct); }
                catch (TaskCanceledException) { break; }
            }
        }

        while (pendingWrites.TryDequeue(out var entry))
            buffer.Add(entry);
        if (buffer.Count > 0)
            FlushBuffer(buffer);
    }

    private static void FlushBuffer(List<CacheEntry> buffer)
    {
        if (buffer.Count == 0) return;

        lock (cacheWriteLock)
        {
            var locationEntries = new List<CacheEntry>();
            var ispEntries = new List<CacheEntry>();
            var generalEntries = new List<CacheEntry>();

            foreach (var entry in buffer)
            {
                switch (entry.Type)
                {
                    case CacheType.Location: locationEntries.Add(entry); break;
                    case CacheType.Isp: ispEntries.Add(entry); break;
                    default: generalEntries.Add(entry); break;
                }
            }

            if (locationEntries.Count > 0)
                AppendToFile(locationCachePath, locationEntries);
            if (ispEntries.Count > 0)
                AppendToFile(ispCachePath, ispEntries);
            if (generalEntries.Count > 0)
                AppendToFile(generalCachePath, generalEntries);
        }
    }

    private static void AppendToFile(string path, List<CacheEntry> entries)
    {
        using var writer = new StreamWriter(path, true, Encoding.UTF8);
        foreach (var e in entries)
            writer.WriteLine($"{e.Key}\t{e.Value}");
    }

    class LineRecord
    {
        public string IpCidr { get; set; } = "";
        public string[] Parts { get; set; } = Array.Empty<string>();

        public string Country => Parts.Length > IdxCountry ? Parts[IdxCountry] : "";
        public string Region => Parts.Length > IdxRegion ? Parts[IdxRegion] : "";
        public string City => Parts.Length > IdxCity ? Parts[IdxCity] : "";
        public string District => Parts.Length > IdxDistrict ? Parts[IdxDistrict] : "";
        public string OwnerDomain => Parts.Length > IdxOwnerDomain ? Parts[IdxOwnerDomain] : "";
        public string IspDomain => Parts.Length > IdxIspDomain ? Parts[IdxIspDomain] : "";

        public string GetLocationKey()
        {
            return $"{Country}|{Region}|{City}|{District}";
        }

        public IEnumerable<string> GetOtherTranslatableFields()
        {
            if (!string.IsNullOrEmpty(OwnerDomain)) yield return OwnerDomain;
        }
    }
}
