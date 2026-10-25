using System.Diagnostics;

namespace Navlonix.LocalDocuments;

/// <summary>
/// Yardımcı SÜREÇ kapasitesi (PDF ve Tesseract çalıştırıcıları için ortak). İlke: bir yuva, o yuvayı kullanan sürecin ÇIKIŞI DOĞRULANMADAN yeniden
/// kullanılabilir sayılmaz. Çıkış istek süresinde doğrulanamazsa süreç <see cref="Orphan"/> ile bu kapıya devredilir: yuva ve süreçle ilgili kaynaklar
/// (geçici dizin, süreç nesnesi) süreç GERÇEKTEN bitene dek tutulur; bitiş <see cref="Process.Exited"/> olayıyla (ya da bir sonraki dolu-kapasite
/// taramasıyla) görülünce kaynaklar temizlenir ve yuva YALNIZ BİR KEZ serbest bırakılır. Kapasite doluysa yeni süreç BAŞLATILMAZ (çağıran Busy döner).
/// <para>
/// Sınırlılık: yetim kayıt sayısı kapasiteyle sınırlıdır (her yetim bir yuva tutar); arka plan görevi, zamanlayıcı ya da yoklama döngüsü YOKTUR (yalnız
/// işletim sistemi çıkış olayı ve dolu kapasitede istek başına tek tarama). Bir sürecin çıkışı hiç görülmezse o yuva kalıcı olarak kaybolur: bu bir
/// KAPASİTE AZALMASI olarak <see cref="Orphaned"/> ile görünür (ve loglanır); kapasite aşılarak yeni süreç başlatılmaz.
/// </para>
/// </summary>
internal sealed class ProcessSlotGate
{
    private readonly SemaphoreSlim _sem;
    private readonly object _lock = new();
    private readonly List<OrphanRecord> _orphans = [];
    private readonly ILogger? _logger;
    private readonly string _name;
    private int _orphaned;
    private long _unverifiedTotal, _recoveredTotal;

    private readonly bool _retryKillOnSweep;

    /// <param name="retryKillOnSweep">true: dolu kapasitede yapılan taramada, hâlâ çalışan yetim sürece (en çok saniyede bir) yeniden sonlandırma denenir.
    /// Yalnız testler, "sonlandırılamayan süreç" benzetimi için false verir.</param>
    public ProcessSlotGate(int capacity, ILogger? logger = null, string name = "process", bool retryKillOnSweep = true)
    {
        _retryKillOnSweep = retryKillOnSweep;
        Capacity = Math.Max(1, capacity);
        _sem = new SemaphoreSlim(Capacity, Capacity);
        _logger = logger;
        _name = name;
    }

    public int Capacity { get; }
    /// <summary>Şu an yeni iş başlatabilecek boş yuva sayısı.</summary>
    public int Available => _sem.CurrentCount;
    /// <summary>Çıkışı doğrulanamamış ve yuvasını hâlâ tutan süreç sayısı (= şu an kaybedilmiş kapasite).</summary>
    public int Orphaned => Volatile.Read(ref _orphaned);
    /// <summary>Şimdiye dek çıkışı istek süresinde doğrulanamayıp yetim bırakılan süreç sayısı.</summary>
    public long UnverifiedTotal => Interlocked.Read(ref _unverifiedTotal);
    /// <summary>Yetim bırakılıp SONRADAN çıkışı doğrulanarak kapasitesi geri kazanılan süreç sayısı.</summary>
    public long RecoveredTotal => Interlocked.Read(ref _recoveredTotal);

    /// <summary>Boş yuva varsa bir kira verir; yoksa (önce yetimleri tarayıp) null. Süreç başlatmadan ÖNCE çağrılmalıdır.</summary>
    public Lease? TryAcquire()
    {
        if (_sem.Wait(0)) return new Lease(this);
        if (Orphaned > 0)
        {
            Sweep();
            if (_sem.Wait(0)) return new Lease(this);
        }
        return null;
    }

    /// <summary>Yuvanın YALNIZ BİR KEZ serbest bırakılmasını garanti eden kira (çift serbest bırakma kapasiteyi aşırmaz).</summary>
    public sealed class Lease
    {
        private readonly ProcessSlotGate _gate;
        private int _released;
        internal Lease(ProcessSlotGate gate) => _gate = gate;

        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) _gate._sem.Release();
        }
    }

    private sealed class OrphanRecord(Process process, Lease lease, Action onExited)
    {
        public readonly Process Process = process;
        public readonly Lease Lease = lease;
        public readonly Action OnExited = onExited;
        public int Done;
        public long LastKillTicks;
    }

    /// <summary>
    /// Çıkışı doğrulanamayan süreci devralır: yuva ve <paramref name="onExited"/> (kaynak temizliği) süreç bitene dek tutulur.
    /// <paramref name="onExited"/> süreç bittiğinde tam bir kez çalışır, SONRA yuva serbest bırakılır.
    /// </summary>
    public void Orphan(Lease lease, Process process, Action onExited)
    {
        var rec = new OrphanRecord(process, lease, onExited);
        lock (_lock) _orphans.Add(rec);
        var now = Interlocked.Increment(ref _orphaned);
        Interlocked.Increment(ref _unverifiedTotal);
        // Yalnız olgular loglanır (içerik, dosya adı, süreç çıktısı yok).
        _logger?.LogWarning("{Kind} yardımcı sürecinin çıkışı doğrulanamadı: yuva ve geçici kaynaklar süreç bitene dek tutuluyor; kapasite {Available}/{Capacity} kullanılabilir, {Orphaned} yuva kayıp.",
            _name, Available, Capacity, now);

        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => Complete(rec);
        }
        catch { /* süreç nesnesi kullanılamıyor: tarama (Sweep) HasExited ile yakalar */ }
        if (SafeExited(process)) Complete(rec);   // olay kaydından önce bitmiş olabilir
    }

    private void Complete(OrphanRecord rec)
    {
        if (Interlocked.Exchange(ref rec.Done, 1) != 0) return;   // olay + tarama + istek yarışında tek kazanan
        lock (_lock) _orphans.Remove(rec);
        try { rec.OnExited(); } catch { /* temizlik hatası yuvayı tutmamalı; çağıran sayaçlarıyla raporlar */ }
        rec.Lease.Release();
        // Sayaçlar yuva serbest bırakıldıktan SONRA güncellenir: "Orphaned == 0" gözlemi, kapasitenin gerçekten geri geldiği anlamına gelir.
        Interlocked.Increment(ref _recoveredTotal);
        Interlocked.Decrement(ref _orphaned);
        _logger?.LogInformation("{Kind} yardımcı sürecinin gecikmiş çıkışı doğrulandı: kaynaklar temizlendi, yuva serbest bırakıldı ({Available}/{Capacity}).", _name, Available, Capacity);
    }

    /// <summary>Dolu kapasitede istek başına tek geçiş: biten yetimleri tamamlar, bitmeyenlere (en çok saniyede bir) yeniden sonlandırma dener. Görev/zamanlayıcı oluşturmaz.</summary>
    public void Sweep()
    {
        OrphanRecord[] snapshot;
        lock (_lock) snapshot = [.. _orphans];
        foreach (var rec in snapshot)
        {
            if (SafeExited(rec.Process)) { Complete(rec); continue; }
            if (!_retryKillOnSweep) continue;
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref rec.LastKillTicks);
            if (now - last < 1000 || Interlocked.CompareExchange(ref rec.LastKillTicks, now, last) != last) continue;
            try { rec.Process.Kill(entireProcessTree: true); } catch { /* bitmiş olabilir ya da sonlandırılamıyor */ }
            if (SafeExited(rec.Process)) Complete(rec);
        }
    }

    private static bool SafeExited(Process p)
    {
        try { return p.HasExited; } catch { return false; }
    }
}

internal static class ProcessTermination
{
    /// <summary>Süreç ağacını sonlandırır ve çıkışını EN ÇOK <paramref name="wait"/> kadar gözler. true = çıkış DOĞRULANDI.</summary>
    public static async Task<bool> KillAndVerifyAsync(Process p, TimeSpan wait)
    {
        // WaitForExitAsync yalnız ana süreci bekler. Linux'ta Kill(entireProcessTree) ile öldürülen
        // çocuklar birkaç an daha çalışabilir; yuva ancak onlar da bittikten sonra bırakılmalı.
        var descendants = LinuxDescendants(p.Id);
        using var cts = new CancellationTokenSource(wait);
        try { p.Kill(entireProcessTree: true); } catch { /* zaten bitmiş olabilir */ }
        try
        {
            await p.WaitForExitAsync(cts.Token);
            while (!descendants.All(LinuxProcessTerminated))
                await Task.Delay(10, cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            try { return p.HasExited && descendants.All(LinuxProcessTerminated); } catch { return false; }
        }
        catch { return false; }
    }

    private static int[] LinuxDescendants(int rootPid)
    {
        if (!OperatingSystem.IsLinux()) return [];
        var found = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(rootPid);
        while (pending.TryPop(out var pid))
        {
            try
            {
                var children = File.ReadAllText($"/proc/{pid}/task/{pid}/children");
                foreach (var raw in children.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (int.TryParse(raw, out var child) && found.Add(child)) pending.Push(child);
            }
            catch (IOException) { /* süreç çıkmış olabilir */ }
            catch (UnauthorizedAccessException) { /* doğrulanamayan çocuk eklenemez */ }
        }
        return [.. found];
    }

    private static bool LinuxProcessTerminated(int pid)
    {
        var path = $"/proc/{pid}/stat";
        if (!File.Exists(path)) return true;
        try
        {
            var stat = File.ReadAllText(path);
            var end = stat.LastIndexOf(')');
            return end >= 0 && end + 2 < stat.Length && stat[end + 2] is 'Z' or 'X';
        }
        catch (FileNotFoundException) { return true; }
        catch { return false; }
    }
}

