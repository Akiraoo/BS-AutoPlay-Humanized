using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutoReplay.Core
{
    /// <summary>一个谱面难度的星数. BL 对 unranked 也有 AI 估计星数; SS 只有 ranked 才有.</summary>
    public sealed class StarInfo
    {
        public float? blStars, blPass, blAcc, blTech;
        public int? blMaxScore;            // 谱面满分 (算 acc 用)
        public int blStatus = -1;          // BL: 0 unranked, 1 nominated, 2 qualified, 3 ranked ...
        public float? ssStars;             // SS ranked 才有 (>0)
        public bool ssRanked;
        public bool blChecked, ssChecked;  // 查过 (就算没有数据也算)
        public long fetchedUnix;
    }

    /// <summary>
    /// 查 BeatLeader / ScoreSaber 公开 API 拿星数 (不需要登入). 结果缓存在本机 JSON, 30 天后才重查.
    /// 遵守限流: BL 每 10 秒 50 次, SS 每 10 秒 100 次 -> 这里各自留余量.
    /// 网络失败不会抛异常, 只是没有星数.
    /// </summary>
    public sealed class StarService
    {
        const string UserAgent = "BS-AutoPlay-Humanized";
        const int BlIntervalMs = 260, SsIntervalMs = 140, CacheDays = 30;
        static readonly Dictionary<string, int> SsDiff = new Dictionary<string, int>
        {
            ["Easy"] = 1, ["Normal"] = 3, ["Hard"] = 5, ["Expert"] = 7, ["ExpertPlus"] = 9,
        };

        readonly string _cachePath;
        readonly Dictionary<string, StarInfo> _cache;
        readonly object _lock = new object();
        DateTime _lastBl = DateTime.MinValue, _lastSs = DateTime.MinValue;
        int _netFailures;
        bool _dirty;

        public bool Offline => _netFailures >= 3;   // 连续失败就当作没网, 不再尝试

        public StarService(string cachePath)
        {
            _cachePath = cachePath;
            _cache = new Dictionary<string, StarInfo>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (cachePath != null && File.Exists(cachePath))
                {
                    var d = JsonConvert.DeserializeObject<Dictionary<string, StarInfo>>(File.ReadAllText(cachePath));
                    if (d != null) foreach (var kv in d) _cache[kv.Key] = kv.Value;
                }
            }
            catch { }
        }

        static string Key(string hash, string diff) => (hash ?? "").ToUpperInvariant() + "|" + diff;

        /// <summary>只看缓存, 不连网 (预览用)</summary>
        public StarInfo Cached(string hash, string diff)
        {
            lock (_lock) return _cache.TryGetValue(Key(hash, diff), out var s) ? s : null;
        }

        /// <summary>查询 (缓存优先). network=false 时只看缓存.</summary>
        public StarInfo Get(string hash, string diff, bool network = true, CancellationToken cancel = default)
        {
            if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(diff)) return null;
            if (hash.Length > 40) hash = hash.Substring(0, 40);
            StarInfo s = Cached(hash, diff);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            bool fresh = s != null && now - s.fetchedUnix < CacheDays * 86400L;
            if (!network || Offline || (fresh && s.blChecked && s.ssChecked && (s.blStars == null || s.blMaxScore != null))) return s;

            s = s ?? new StarInfo();
            if (!fresh || !s.blChecked || (s.blStars != null && s.blMaxScore == null))   // 旧缓存没有满分, 补查
            {
                bool ok = FetchBl(hash, cancel);
                s = Cached(hash, diff) ?? s;
                if (ok) s.blChecked = true;
            }
            if (!fresh || !s.ssChecked) FetchSs(hash, diff, s, cancel);
            s.fetchedUnix = now;
            lock (_lock) { _cache[Key(hash, diff)] = s; _dirty = true; }
            return s;
        }

        // BL 一次回传这首歌所有难度, 一起写进缓存
        bool FetchBl(string hash, CancellationToken cancel)
        {
            Wait(ref _lastBl, BlIntervalMs, cancel);
            string body = Http($"https://api.beatleader.com/map/hash/{hash}", out int code);
            if (body == null) return code == 404;      // 404 = BL 没有这首歌, 也算查过
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            try
            {
                var diffs = JObject.Parse(body)["difficulties"] as JArray;
                if (diffs == null) return true;
                foreach (var d in diffs)
                {
                    if ((string)d["modeName"] != "Standard") continue;
                    string dn = (string)d["difficultyName"];
                    var key = Key(hash, dn);
                    StarInfo s;
                    lock (_lock) { if (!_cache.TryGetValue(key, out s)) _cache[key] = s = new StarInfo(); }
                    s.blChecked = true;
                    s.blStatus = (int?)d["status"] ?? -1;
                    s.blStars = Num(d["stars"]); s.blPass = Num(d["passRating"]); s.blAcc = Num(d["accRating"]); s.blTech = Num(d["techRating"]);
                    s.blMaxScore = (int?)d["maxScore"];
                    if (s.fetchedUnix == 0) s.fetchedUnix = now;
                }
                lock (_lock) _dirty = true;
                return true;
            }
            catch { return false; }
        }

        void FetchSs(string hash, string diff, StarInfo s, CancellationToken cancel)
        {
            if (!SsDiff.TryGetValue(diff, out int n)) { s.ssChecked = true; return; }
            Wait(ref _lastSs, SsIntervalMs, cancel);
            string body = Http($"https://scoresaber.com/api/leaderboard/by-hash/{hash}/info?difficulty={n}&gameMode=SoloStandard", out int code);
            if (body == null) { if (code == 404) s.ssChecked = true; return; }
            try
            {
                var o = JObject.Parse(body);
                s.ssRanked = (bool?)o["ranked"] ?? false;
                float? st = Num(o["stars"]);
                s.ssStars = s.ssRanked && st > 0 ? st : null;
                s.ssChecked = true;
            }
            catch { }
        }

        static float? Num(JToken t) => t == null || t.Type == JTokenType.Null ? (float?)null : (float)t;

        void Wait(ref DateTime last, int ms, CancellationToken cancel)
        {
            var dt = (last.AddMilliseconds(ms) - DateTime.UtcNow).TotalMilliseconds;
            if (dt > 0) cancel.WaitHandle.WaitOne((int)dt);
            cancel.ThrowIfCancellationRequested();
            last = DateTime.UtcNow;
        }

        string Http(string url, out int status)
        {
            status = 0;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = UserAgent;
                req.Timeout = 10000;
                req.ReadWriteTimeout = 10000;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream()))
                {
                    status = (int)resp.StatusCode;
                    _netFailures = 0;
                    return sr.ReadToEnd();
                }
            }
            catch (WebException ex) when (ex.Response is HttpWebResponse r)
            {
                status = (int)r.StatusCode;
                if (status == 429) Thread.Sleep(2000);     // 被限流: 等一下, 这次当作没数据
                else _netFailures = 0;
                return null;
            }
            catch
            {
                _netFailures++;
                return null;
            }
        }

        public void Save()
        {
            if (_cachePath == null) return;
            lock (_lock)
            {
                if (!_dirty) return;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_cachePath));
                    File.WriteAllText(_cachePath, JsonConvert.SerializeObject(_cache));
                    _dirty = false;
                }
                catch { }
            }
        }
    }
}
