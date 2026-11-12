using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ZapretGui.Core
{
    /// <summary>Один источник файла: GitHub или его зеркало.</summary>
    public sealed class UpdateSource
    {
        public UpdateSource(string url, string name, bool isMirror)
        {
            Url = url;
            Name = name;
            IsMirror = isMirror;
        }

        public string Url { get; }
        public string Name { get; }
        public bool IsMirror { get; }

        /// <summary>Результат быстрой пробы (Range 0-0): поддерживает ли сервер докачку.</summary>
        public bool SupportsRanges { get; internal set; }
        /// <summary>Соединение прошло только без системного прокси (частая поломка после VPN).</summary>
        public bool UseDirect { get; internal set; }
        public long ProbeMs { get; internal set; } = long.MaxValue;
        public bool ProbeOk { get; internal set; }
    }

    /// <summary>Прогресс загрузки: скорость, остаток и текущий источник — для окна обновления.</summary>
    public sealed class UpdateDownloadInfo
    {
        public long Received { get; init; }
        public long Total { get; init; }
        public double BytesPerSecond { get; init; }
        public string SourceName { get; init; } = "";
        public int Connections { get; init; }

        public double Percent => Total > 0 ? Received * 100.0 / Total : 0;
        public TimeSpan? Remaining => BytesPerSecond > 1 && Total > Received
            ? TimeSpan.FromSeconds((Total - Received) / BytesPerSecond)
            : null;

        public string SpeedText => BytesPerSecond <= 0
            ? ""
            : BytesPerSecond >= 1024 * 1024
                ? (BytesPerSecond / 1024.0 / 1024.0).ToString("0.0") + " МБ/с"
                : (BytesPerSecond / 1024.0).ToString("0") + " КБ/с";

        public string RemainingText => Remaining is { } left
            ? "осталось ~" + FormatDuration(left)
            : "";

        public static string FormatDuration(TimeSpan value)
        {
            if (value.TotalSeconds < 1) return "меньше секунды";
            if (value.TotalSeconds < 60) return ((int)Math.Ceiling(value.TotalSeconds)) + " с";
            if (value.TotalMinutes < 60) return ((int)Math.Ceiling(value.TotalMinutes)) + " мин";
            return ((int)Math.Round(value.TotalHours)) + " ч";
        }
    }

    /// <summary>
    /// Ручное управление загрузкой из окна обновления: «Ускорить через зеркало».
    /// Работающий загрузчик проверяет флаг между попытками и переключает источник, не теряя скачанного.
    /// </summary>
    public sealed class UpdateDownloadControl
    {
        private int _preferMirror;
        private int _mirrorSwitches;

        public bool MirrorRequested => Volatile.Read(ref _preferMirror) != 0;
        public int MirrorSwitches => Volatile.Read(ref _mirrorSwitches);

        public void PreferMirror() => Interlocked.Exchange(ref _preferMirror, 1);
        public void Reset() => Interlocked.Exchange(ref _preferMirror, 0);
        internal void CountSwitch() => Interlocked.Increment(ref _mirrorSwitches);
    }

    /// <summary>
    /// Загрузка файлов с GitHub с докачкой и зеркалами.
    ///
    /// Чем отличается от прежней реализации (v2.1.1):
    ///  • файл режется на фрагменты и качается 4 соединениями параллельно;
    ///  • при обрыве фрагмент продолжается с последнего записанного байта (Range), а не с нуля;
    ///  • состояние фрагментов лежит рядом в *.part.json, поэтому «Отмена» и «Повторить»
    ///    продолжают загрузку, а не начинают её заново;
    ///  • источники (GitHub + зеркала) пробуются наперёд и сортируются по задержке,
    ///    при простое дольше 45 с источник меняется автоматически;
    ///  • целостность гарантирует SHA-256 из GitHub API — зеркало не может подменить файл.
    /// </summary>
    public static class UpdateDownloader
    {
        public const int MaxConnections = 4;
        private const long MinChunkSize = 4L * 1024 * 1024;
        private const int ChunkAttempts = 5;
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(400);

        /// <summary>Живые зеркала GitHub (префикс + полный адрес файла).</summary>
        public static readonly (string Prefix, string Name)[] Mirrors = new[]
        {
            ("https://ghproxy.net/", "ghproxy.net"),
            ("https://gh-proxy.com/", "gh-proxy.com"),
            ("https://ghfast.top/", "ghfast.top"),
            ("https://github.moeyy.xyz/", "moeyy.xyz"),
            ("https://gh.llkk.cc/", "llkk.cc")
        };

        private static readonly HttpClient ClientWithProxy = CreateClient(true);
        private static readonly HttpClient ClientWithoutProxy = CreateClient(false);
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

        public static UpdateSource GitHub(string url) => new(url, "GitHub", false);

        /// <summary>GitHub + зеркала для файла из релиза.</summary>
        public static List<UpdateSource> BuildSources(string url, bool useMirrors, bool preferMirror = false)
        {
            var sources = new List<UpdateSource> { GitHub(url) };
            if (useMirrors && Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var (prefix, name) in Mirrors)
                    sources.Add(new UpdateSource(prefix + url, name, true));
            }
            return preferMirror ? sources.OrderBy(s => s.IsMirror ? 0 : 1).ToList() : sources;
        }

        private static HttpClient CreateClient(bool useProxy)
        {
            var handler = new SocketsHttpHandler
            {
                UseProxy = useProxy,
                AllowAutoRedirect = true,
                // Никакой декомпрессии: Range-запросы обязаны считаться в байтах файла, а не потока.
                AutomaticDecompression = DecompressionMethods.None,
                ConnectTimeout = TimeSpan.FromSeconds(15),
                MaxConnectionsPerServer = MaxConnections * 2,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                SslOptions = { EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12
                    | System.Security.Authentication.SslProtocols.Tls13 }
            };
            var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ZapretGUI", "1.0"));
            return client;
        }

        /// <summary>Разбиение файла на фрагменты — чистая функция, покрыта CoreLogicHarness.</summary>
        public static List<(long Start, long End)> PlanChunks(long total, int connections)
        {
            var result = new List<(long Start, long End)>();
            if (total <= 0) return result;
            var max = Math.Max(1, Math.Min(connections, MaxConnections));
            var count = Math.Max(1, Math.Min(max, (int)Math.Ceiling(total / (double)MinChunkSize)));
            var size = total / count;
            var offset = 0L;
            for (var i = 0; i < count; i++)
            {
                var end = i == count - 1 ? total : offset + size;
                result.Add((offset, end));
                offset = end;
            }
            return result;
        }

        /// <summary>
        /// Скачивает файл. Частичный результат остаётся в <paramref name="partPath"/>,
        /// поэтому повторный вызов продолжает с того же места.
        /// </summary>
        public static async Task DownloadAsync(
            string partPath,
            long expectedSize,
            IReadOnlyList<UpdateSource> sources,
            IProgress<UpdateDownloadInfo>? progress,
            UpdateDownloadControl? control,
            CancellationToken ct)
        {
            if (sources.Count == 0) throw new ArgumentException("Нет источников загрузки", nameof(sources));
            AppPaths.EnsureDir(Path.GetDirectoryName(partPath) ?? AppPaths.TempDir);

            var probed = await ProbeAsync(sources, expectedSize, ct).ConfigureAwait(false);
            if (probed.Count == 0)
                throw new IOException("Ни один источник не ответил: " + string.Join(", ", sources.Select(s => s.Name)));

            var ranged = probed.Where(s => s.SupportsRanges).OrderBy(s => s.ProbeMs).ToList();
            if (ranged.Count == 0)
            {
                // Зеркало/сервер не умеет Range — качаем одним потоком с самого быстрого источника.
                await DownloadSequentialAsync(partPath, expectedSize, probed.OrderBy(s => s.ProbeMs).ToList(), progress, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                await DownloadRangedAsync(partPath, expectedSize, ranged, progress, control, ct).ConfigureAwait(false);
            }

            var length = new FileInfo(partPath).Length;
            if (expectedSize > 0 && length != expectedSize)
                throw new InvalidDataException($"Размер файла не совпадает: получено {length}, ожидалось {expectedSize} байт.");
        }

        // ------------------------------------------------------------------ проба источников

        private static async Task<List<UpdateSource>> ProbeAsync(
            IReadOnlyList<UpdateSource> sources, long expectedSize, CancellationToken ct)
        {
            var tasks = sources.Select((source, index) => ProbeOneAsync(source, expectedSize, index, ct)).ToList();
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            var alive = results.Where(s => s.ProbeOk).ToList();
            if (alive.Count > 1)
                AppLog.Info("[Загрузка] Источники: " + string.Join(", ",
                    alive.OrderBy(s => s.ProbeMs).Select(s => $"{s.Name} {s.ProbeMs} мс{(s.SupportsRanges ? "" : " (без докачки)")}")));
            return alive;
        }

        private static async Task<UpdateSource> ProbeOneAsync(
            UpdateSource source, long expectedSize, int index, CancellationToken ct)
        {
            // Каждое следующее зеркало стартует чуть позже: не бомбим GitHub пачкой запросов.
            if (index > 0) await Task.Delay(Math.Min(400, index * 120), ct).ConfigureAwait(false);
            var sw = Stopwatch.StartNew();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(ProbeTimeout);
                using var response = await SendProbeAsync(source, cts.Token).ConfigureAwait(false);
                sw.Stop();

                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    var range = response.Content.Headers.ContentRange;
                    if (expectedSize > 0 && range?.Length is { } total && total != expectedSize)
                    {
                        AppLog.Warn($"[Загрузка] {source.Name} отдаёт другой файл ({total} вместо {expectedSize} байт) — пропускаю");
                        return source;
                    }
                    source.SupportsRanges = true;
                    source.ProbeOk = true;
                    source.ProbeMs = sw.ElapsedMilliseconds;
                }
                else if (response.StatusCode == HttpStatusCode.OK)
                {
                    // Range проигнорирован: источник годится только для последовательной загрузки.
                    var length = response.Content.Headers.ContentLength;
                    if (expectedSize > 0 && length is { } size && size != expectedSize)
                    {
                        AppLog.Warn($"[Загрузка] {source.Name} отдаёт другой файл ({size} вместо {expectedSize} байт) — пропускаю");
                        return source;
                    }
                    source.SupportsRanges = false;
                    source.ProbeOk = true;
                    source.ProbeMs = sw.ElapsedMilliseconds;
                }
                else
                {
                    AppLog.Warn($"[Загрузка] {source.Name} ответил {(int)response.StatusCode} — пропускаю");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                AppLog.Warn($"[Загрузка] {source.Name} недоступен: {GuiUpdateService.Describe(ex)}");
            }
            return source;
        }

        /// <summary>GET с Range 0-0: через системный прокси, а при сбое — напрямую (битый прокси после VPN).</summary>
        private static async Task<HttpResponseMessage> SendProbeAsync(UpdateSource source, CancellationToken ct)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            try
            {
                return await ClientWithProxy.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                request.Dispose();
                var direct = new HttpRequestMessage(HttpMethod.Get, source.Url);
                direct.Headers.Range = new RangeHeaderValue(0, 0);
                var response = await ClientWithoutProxy.SendAsync(direct, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                source.UseDirect = true;
                AppLog.Info($"[Загрузка] {source.Name} доступен только без системного прокси");
                return response;
            }
        }

        // ------------------------------------------------------------------ параллельная докачка

        private sealed class Chunk
        {
            public long Start;
            public long End;          // не включая
            public long Done;         // уже записано в файл
            public int Attempts;
            public bool Claimed;
            public long Length => End - Start;
            public bool Complete => Done >= Length;
        }

        private sealed class PartState
        {
            public long Total { get; set; }
            public List<long> Chunks { get; set; } = new();
            public List<long> Done { get; set; } = new();
        }

        private static string StatePath(string partPath) => partPath + ".json";

        private static List<Chunk>? LoadState(string partPath, long total)
        {
            try
            {
                var path = StatePath(partPath);
                if (!File.Exists(path) || !File.Exists(partPath)) return null;
                var state = JsonSerializer.Deserialize<PartState>(File.ReadAllText(path), JsonOptions);
                if (state == null || state.Total != total || state.Chunks.Count == 0) return null;
                if (state.Chunks.Count != state.Done.Count) return null;
                if (state.Chunks[state.Chunks.Count - 1] != total) return null;
                var chunks = new List<Chunk>();
                for (var i = 0; i < state.Chunks.Count; i++)
                {
                    var start = i == 0 ? 0 : state.Chunks[i - 1];
                    var end = state.Chunks[i];
                    var done = state.Done[i];
                    // Битое состояние (обрезанный json, чужой файл) — начинаем заново, а не портим данные.
                    if (end <= start || done < 0 || done > end - start) return null;
                    chunks.Add(new Chunk { Start = start, End = end, Done = done });
                }
                if (new FileInfo(partPath).Length < total) return null;
                var filled = chunks.Sum(c => c.Done);
                if (filled > 0) AppLog.Info($"[Загрузка] Продолжаю с {filled / 1024.0 / 1024.0:0.0} МБ из {total / 1024.0 / 1024.0:0.0} МБ");
                return chunks;
            }
            catch (Exception ex)
            {
                AppLog.Warn("[Загрузка] Не удалось прочитать состояние загрузки: " + ex.Message);
                return null;
            }
        }

        private static readonly object StateGate = new();

        private static void SaveState(string partPath, IReadOnlyList<Chunk> chunks, long total)
        {
            lock (StateGate)
            {
                SaveStateCore(partPath, chunks, total);
            }
        }

        private static void SaveStateCore(string partPath, IReadOnlyList<Chunk> chunks, long total)
        {
            try
            {
                var state = new PartState
                {
                    Total = total,
                    Chunks = chunks.Select(c => c.End).ToList(),
                    Done = chunks.Select(c => c.Done).ToList()
                };
                var path = StatePath(partPath);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOptions));
                File.Move(tmp, path, true);
            }
            catch (Exception ex)
            {
                AppLog.Warn("[Загрузка] Не удалось сохранить состояние загрузки: " + ex.Message);
            }
        }

        private static async Task DownloadRangedAsync(
            string partPath, long expectedSize, List<UpdateSource> sources,
            IProgress<UpdateDownloadInfo>? progress, UpdateDownloadControl? control, CancellationToken ct)
        {
            var chunks = expectedSize > 0 ? LoadState(partPath, expectedSize) : null;
            if (chunks == null)
            {
                TryDelete(partPath);
                if (expectedSize <= 0)
                    throw new IOException("GitHub не сообщил размер файла — докачка невозможна, попробуйте ещё раз.");
                chunks = PlanChunks(expectedSize, MaxConnections).Select(r => new Chunk { Start = r.Start, End = r.End }).ToList();
                using (var file = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                    file.SetLength(expectedSize);
                SaveState(partPath, chunks, expectedSize);
            }

            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var state = new DownloadState(chunks, expectedSize, partPath, sources, progress, control);
            state.StartReporter(runCts.Token);
            var workers = Enumerable.Range(0, Math.Min(MaxConnections, chunks.Count))
                .Select(_ => Task.Run(() => WorkerLoopAsync(state, runCts.Token), CancellationToken.None))
                .ToArray();
            try
            {
                await Task.WhenAll(workers).ConfigureAwait(false);
            }
            catch
            {
                // Останавливаем остальные соединения, чтобы они не писали в файл после выхода.
                runCts.Cancel();
                try { await Task.WhenAll(workers).ConfigureAwait(false); } catch { }
                throw;
            }
            finally
            {
                await state.StopReporterAsync().ConfigureAwait(false);
            }

            var missing = chunks.Where(c => !c.Complete).Sum(c => c.Length - c.Done);
            if (missing > 0)
                throw new IOException($"Не удалось докачать {missing / 1024.0 / 1024.0:0.0} МБ. Нажмите «Повторить» — загрузка продолжится.");
        }

        private sealed class DownloadState
        {
            private readonly object _gate = new();
            private readonly List<Chunk> _chunks;
            private readonly long _total;
            private readonly string _partPath;
            private readonly List<UpdateSource> _sources;
            private readonly IProgress<UpdateDownloadInfo>? _progress;
            private readonly UpdateDownloadControl? _control;
            private int _sourceIndex;
            private long _received;
            private long _lastSample;
            private long _lastSampleTicks = Stopwatch.GetTimestamp();
            private double _speed;
            private CancellationTokenSource? _reporterCts;
            private Task? _reporter;

            public DownloadState(List<Chunk> chunks, long total, string partPath, List<UpdateSource> sources,
                IProgress<UpdateDownloadInfo>? progress, UpdateDownloadControl? control)
            {
                _chunks = chunks;
                _total = total;
                _partPath = partPath;
                _sources = sources;
                _progress = progress;
                _control = control;
                _received = chunks.Sum(c => c.Done);
                _lastSample = _received;
            }

            public UpdateSource CurrentSource
            {
                get { lock (_gate) return _sources[Math.Min(_sourceIndex, _sources.Count - 1)]; }
            }

            public Chunk? TakeNext()
            {
                lock (_gate)
                {
                    if (_control?.MirrorRequested == true && _sourceIndex == 0 && _sources.Count > 1 &&
                        !_sources[0].IsMirror)
                    {
                        _sourceIndex = 1;
                        _control.CountSwitch();
                        AppLog.Info("[Загрузка] По запросу пользователя переключаюсь на зеркало " + _sources[1].Name);
                    }
                    foreach (var chunk in _chunks)
                    {
                        if (chunk.Complete || chunk.Claimed) continue;
                        chunk.Claimed = true;
                        return chunk;
                    }
                    return null;
                }
            }

            public bool AdvanceSource()
            {
                lock (_gate)
                {
                    if (_sourceIndex + 1 >= _sources.Count) return false;
                    _sourceIndex++;
                    AppLog.Warn($"[Загрузка] Источник {_sources[_sourceIndex - 1].Name} не отдал данные — перехожу на {_sources[_sourceIndex].Name}");
                    return true;
                }
            }

            public string GetPartPath() => _partPath;

            public void Release(Chunk chunk)
            {
                bool complete;
                lock (_gate)
                {
                    chunk.Claimed = false;
                    complete = chunk.Complete;
                }
                if (complete) SaveState(_partPath, _chunks, _total);
            }

            public void Add(long bytes) => Interlocked.Add(ref _received, bytes);
            public long Received => Interlocked.Read(ref _received);

            public void StartReporter(CancellationToken ct)
            {
                _reporterCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var token = _reporterCts.Token;
                _reporter = Task.Run(async () =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        try { await Task.Delay(ProgressInterval, token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }
                        Report();
                    }
                }, CancellationToken.None);
            }

            public async Task StopReporterAsync()
            {
                try { _reporterCts?.Cancel(); } catch { }
                if (_reporter != null) { try { await _reporter.ConfigureAwait(false); } catch { } }
                Report();
                _reporterCts?.Dispose();
                _reporterCts = null;
            }

            private void Report()
            {
                var now = Stopwatch.GetTimestamp();
                var received = Received;
                var seconds = (now - _lastSampleTicks) / (double)Stopwatch.Frequency;
                if (seconds > 0.2)
                {
                    var instant = (received - _lastSample) / seconds;
                    _speed = _speed <= 0 ? instant : _speed * 0.6 + instant * 0.4;
                    _lastSample = received;
                    _lastSampleTicks = now;
                }
                try
                {
                    _progress?.Report(new UpdateDownloadInfo
                    {
                        Received = received,
                        Total = _total,
                        BytesPerSecond = Math.Max(0, _speed),
                        SourceName = CurrentSource.Name,
                        Connections = Math.Min(_chunks.Count, MaxConnections)
                    });
                }
                catch { }
            }
        }

        private static async Task WorkerLoopAsync(DownloadState state, CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = state.TakeNext();
                if (chunk == null) return;
                var before = chunk.Done;
                try
                {
                    await DownloadChunkAsync(state, chunk, ct).ConfigureAwait(false);
                    state.Release(chunk);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    state.Release(chunk);
                    throw;
                }
                catch (Exception ex)
                {
                    state.Release(chunk);
                    chunk.Attempts++;
                    AppLog.Warn($"[Загрузка] Фрагмент {chunk.Start}-{chunk.End}: " + GuiUpdateService.Describe(ex));
                    if (chunk.Done == before) state.AdvanceSource();
                    if (chunk.Attempts >= ChunkAttempts)
                        throw new IOException("Фрагмент не скачивается: " + GuiUpdateService.Describe(ex), ex);
                    try { await Task.Delay(600, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                }
            }
        }

        private static async Task DownloadChunkAsync(DownloadState state, Chunk chunk, CancellationToken ct)
        {
            var source = state.CurrentSource;
            var client = source.UseDirect ? ClientWithoutProxy : ClientWithProxy;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(StallTimeout);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
                request.Headers.Range = new RangeHeaderValue(chunk.Start + chunk.Done, chunk.End - 1);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.OK && chunk.Start + chunk.Done > 0)
                    throw new NotSupportedException("Источник не поддерживает докачку фрагментов");

                if (response.StatusCode != HttpStatusCode.PartialContent && response.StatusCode != HttpStatusCode.OK)
                    response.EnsureSuccessStatusCode();

                var offset = response.StatusCode == HttpStatusCode.PartialContent ? chunk.Start + chunk.Done : 0;
                await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                await using var file = new FileStream(state.GetPartPath(), FileMode.OpenOrCreate, FileAccess.Write,
                    FileShare.ReadWrite, 128 * 1024, FileOptions.Asynchronous);
                file.Seek(offset, SeekOrigin.Begin);

                var buffer = new byte[128 * 1024];
                while (chunk.Done < chunk.Length)
                {
                    var want = (int)Math.Min(buffer.Length, chunk.Length - chunk.Done);
                    var read = await stream.ReadAsync(buffer.AsMemory(0, want), cts.Token).ConfigureAwait(false);
                    if (read <= 0) break;
                    await file.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
                    chunk.Done += read;
                    state.Add(read);
                    cts.CancelAfter(StallTimeout); // данные идут — продлеваем сторожевой таймер
                }
                await file.FlushAsync(cts.Token).ConfigureAwait(false);
            }
        }

        // ------------------------------------------------------------------ последовательная загрузка

        private static async Task DownloadSequentialAsync(
            string partPath, long expectedSize, List<UpdateSource> sources,
            IProgress<UpdateDownloadInfo>? progress, CancellationToken ct)
        {
            Exception? last = null;
            foreach (var source in sources)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(StallTimeout);
                var started = Stopwatch.StartNew();
                long received = 0;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
                    var client = source.UseDirect ? ClientWithoutProxy : ClientWithProxy;
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                        .ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    var total = response.Content.Headers.ContentLength ?? expectedSize;
                    await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                    await using var file = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.Read,
                        128 * 1024, FileOptions.Asynchronous);
                    var buffer = new byte[128 * 1024];
                    int read;
                    while ((read = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
                        received += read;
                        cts.CancelAfter(StallTimeout);
                        var seconds = Math.Max(0.2, started.Elapsed.TotalSeconds);
                        progress?.Report(new UpdateDownloadInfo
                        {
                            Received = received,
                            Total = total,
                            BytesPerSecond = received / seconds,
                            SourceName = source.Name,
                            Connections = 1
                        });
                    }
                    if (expectedSize > 0 && received != expectedSize)
                        throw new InvalidDataException($"Источник отдал {received} из {expectedSize} байт");
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    last = ex;
                    AppLog.Warn($"[Загрузка] {source.Name} не смог отдать файл: {GuiUpdateService.Describe(ex)}");
                    TryDelete(partPath);
                }
            }
            throw new IOException("Не удалось скачать файл ни с одного источника: " +
                                  GuiUpdateService.Describe(last ?? new IOException("неизвестная ошибка")), last);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
