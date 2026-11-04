using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ample.Server.Lite;

/// <summary>One file of a share: its path in the project, the SHA-256 and size of its content, and whether it is a file of a built-in demo (those are not stored, the server knows them).</summary>
/// <param name="Path">The path in the project.</param>
/// <param name="Sha256">The hash of the content.</param>
/// <param name="Size">The size in bytes.</param>
/// <param name="Demo">True for a file of a built-in demo.</param>
public sealed record ShareFile(string Path, string Sha256, long Size, bool Demo);

/// <summary>What the maker fills in when sharing.</summary>
/// <param name="Title">The title of the song.</param>
/// <param name="Author">The author.</param>
/// <param name="Description">The description (may be empty).</param>
/// <param name="Expires">1h, 24h, 1w, 1m or never.</param>
/// <param name="Rights">True when the maker said that they made it or may share it.</param>
/// <param name="Entry">The song to build.</param>
public sealed record ShareRequest(string Title, string Author, string Description, string Expires, bool Rights, string Entry);

/// <summary>A share as it is stored (the public part: nothing of the maker's address or browser is in it).</summary>
public sealed class ShareRecord
{
    /// <summary>Gets or sets the short id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the author.</summary>
    public string Author { get; set; } = string.Empty;

    /// <summary>Gets or sets the description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets when it was made (UTC).</summary>
    public DateTime Created { get; set; }

    /// <summary>Gets or sets when it expires (UTC), or null for no end.</summary>
    public DateTime? Expires { get; set; }

    /// <summary>Gets or sets the song to build.</summary>
    public string Entry { get; set; } = string.Empty;

    /// <summary>Gets or sets the files.</summary>
    public List<ShareFile> Files { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether the maker or the owner took it down (the files stay on disk).</summary>
    public bool Removed { get; set; }

    /// <summary>Gets or sets a value indicating whether the owner put it in as a permanent share: it never expires, is never pushed out of the store, and has no management code.</summary>
    public bool Permanent { get; set; }
}

/// <summary>What is kept about the maker, for the owner only (abuse): never part of an answer of the public API.</summary>
public sealed class SharePrivate
{
    /// <summary>Gets or sets the address (aged: see <see cref="LiteShares.AgeIp"/>).</summary>
    public string Ip { get; set; } = string.Empty;

    /// <summary>Gets or sets the browser (aged to the kind of browser).</summary>
    public string UserAgent { get; set; } = string.Empty;

    /// <summary>Gets or sets the cookie id of the browser (dropped after 30 days).</summary>
    public string? Cookie { get; set; }

    /// <summary>Gets or sets how far the data has aged: 0 as it was, 1 after 14 days, 2 after 30 days.</summary>
    public int Stage { get; set; }

    /// <summary>Gets or sets the SHA-256 of the management code.</summary>
    public string CodeHash { get; set; } = string.Empty;

    /// <summary>Gets or sets what was logged as "would be removed" (expired, capacity), so that it is said once.</summary>
    public string? Logged { get; set; }

    /// <summary>Gets or sets the changes: time, what.</summary>
    public List<string> Changes { get; set; } = [];
}

/// <summary>The answer of <see cref="LiteShares.Create"/>.</summary>
/// <param name="Id">The short id of the share.</param>
/// <param name="Code">The management code (shown once).</param>
/// <param name="Expires">When it expires, or null.</param>
public sealed record ShareCreated(string Id, string Code, DateTime? Expires);

/// <summary>The limits of the share store.</summary>
/// <param name="StoreBytes">The bytes that live shares may hold together (what does not fit goes, the oldest first, as far as the answer of a check goes: nothing is deleted yet).</param>
/// <param name="CeilingBytes">The most bytes on disk, expired shares included: above it nothing new is accepted until a cleanup has run.</param>
/// <param name="IpBytesPerHour">The bytes of own files that one address may put in shares in an hour.</param>
public sealed record ShareLimits(long StoreBytes, long CeilingBytes, long IpBytesPerHour);

/// <summary>
/// The store of shared songs (sharing own work on the public Lite). Files are kept by the hash of their content, each share is a manifest. A share "is no longer available" when it expired,
/// was taken down, or does not fit in the store any more (the oldest go first); nothing is deleted from disk by this class: that is decided at the check, and a log says what a cleanup would remove.
/// </summary>
public sealed class LiteShares
{
    private const string IdAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string manifests;
    private readonly string privates;
    private readonly string blobs;
    private readonly string logFile;
    private readonly ShareLimits limits;
    private readonly Func<DateTime> now;
    private readonly object sync = new();
    private readonly Dictionary<string, ShareRecord> shares = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SharePrivate> secrets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> blobSizes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(DateTime At, long Bytes)>> hourly = new(StringComparer.Ordinal);
    private HashSet<string> excess = new(StringComparer.Ordinal);
    private DateTime computed = DateTime.MinValue;
    private bool dirty = true;

    /// <summary>Opens (and, the first time, makes) the store.</summary>
    /// <param name="root">The folder of the store.</param>
    /// <param name="limits">The limits.</param>
    /// <param name="now">The clock (a test gives its own).</param>
    public LiteShares(string root, ShareLimits limits, Func<DateTime>? now = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        this.limits = limits;
        this.now = now ?? (() => DateTime.UtcNow);
        manifests = Path.Combine(root, "manifests");
        privates = Path.Combine(root, "private");
        blobs = Path.Combine(root, "blobs");
        logFile = Path.Combine(root, "cleanup-log.txt");
        foreach (string folder in new[] { manifests, privates, blobs }) Directory.CreateDirectory(folder);
        foreach (string file in Directory.EnumerateFiles(manifests, "*.json"))
        {
            ShareRecord? record = JsonSerializer.Deserialize<ShareRecord>(File.ReadAllText(file), Json);
            if (record is not null) shares[record.Id] = record;
        }

        foreach (string file in Directory.EnumerateFiles(privates, "*.json"))
        {
            SharePrivate? secret = JsonSerializer.Deserialize<SharePrivate>(File.ReadAllText(file), Json);
            if (secret is not null) secrets[Path.GetFileNameWithoutExtension(file)] = secret;
        }

        foreach (string file in Directory.EnumerateFiles(blobs)) blobSizes[Path.GetFileName(file)] = new FileInfo(file).Length;
    }

    /// <summary>Gets how many shares there are (every state).</summary>
    public int Count
    {
        get
        {
            lock (sync) return shares.Count;
        }
    }

    /// <summary>Gets how many bytes the stored files take on disk (expired shares included: nothing is deleted yet).</summary>
    public long PhysicalBytes
    {
        get
        {
            lock (sync) return blobSizes.Values.Sum();
        }
    }

    /// <summary>The time that a duration name stands for: <c>1h</c>, <c>24h</c>, <c>1w</c>, <c>1m</c> (30 days) or <c>never</c> (no end).</summary>
    /// <param name="name">The name.</param>
    /// <param name="span">The time, or null for no end.</param>
    /// <returns>True when the name is one of these.</returns>
    public static bool TryDuration(string? name, out TimeSpan? span)
    {
        span = name switch
        {
            "1h" => TimeSpan.FromHours(1),
            "24h" => TimeSpan.FromHours(24),
            "1w" => TimeSpan.FromDays(7),
            "1m" => TimeSpan.FromDays(30),
            _ => null,
        };
        return name is "1h" or "24h" or "1w" or "1m" or "never";
    }

    /// <summary>The key under which an address is counted: an IPv4 address as it is, an IPv6 address by its /64 (one household has a whole /64).</summary>
    /// <param name="ip">The address.</param>
    /// <returns>The key.</returns>
    public static string IpKey(string ip)
    {
        if (!IPAddress.TryParse(ip, out IPAddress? address)) return ip;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return address.ToString();
        byte[] bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    /// <summary>An address aged to a stage: stage 1 (after 14 days) drops the last part (IPv4 <c>a.b.c.x</c>, IPv6 the last 64 bits), stage 2 (after 30 days) more (<c>a.b.x.x</c>, IPv6 the last 80 bits).</summary>
    /// <param name="ip">The address.</param>
    /// <param name="stage">0, 1 or 2.</param>
    /// <returns>The aged address as text.</returns>
    public static string AgeIp(string ip, int stage)
    {
        if (stage <= 0 || !IPAddress.TryParse(ip.Split('/')[0], out IPAddress? address)) return ip;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length == 4) return stage == 1 ? $"{bytes[0]}.{bytes[1]}.{bytes[2]}.x" : $"{bytes[0]}.{bytes[1]}.x.x";
        int keep = stage == 1 ? 8 : 6;
        Array.Clear(bytes, keep, 16 - keep);
        return new IPAddress(bytes) + (stage == 1 ? "/64" : "/48");
    }

    /// <summary>A browser text aged to the kind of browser (after 14 days).</summary>
    /// <param name="userAgent">The text the browser sent.</param>
    /// <returns>For example <c>Chrome, mobile</c>.</returns>
    public static string AgeUserAgent(string userAgent)
    {
        string text = userAgent ?? string.Empty;
        string kind = text.Contains("Firefox/", StringComparison.Ordinal) ? "Firefox"
            : text.Contains("Edg/", StringComparison.Ordinal) ? "Edge"
            : text.Contains("Chrome/", StringComparison.Ordinal) ? "Chrome"
            : text.Contains("Safari/", StringComparison.Ordinal) ? "Safari" : "other";
        return text.Contains("Mobile", StringComparison.Ordinal) || text.Contains("Android", StringComparison.Ordinal) ? kind + ", mobile" : kind;
    }

    /// <summary>Makes a share. The files are given with their bytes; those of a built-in demo are listed but not stored.</summary>
    /// <param name="request">What the maker filled in.</param>
    /// <param name="files">The files: path, bytes, whether it is a file of a demo.</param>
    /// <param name="ip">The address of the maker.</param>
    /// <param name="userAgent">The browser of the maker.</param>
    /// <param name="cookie">The cookie id of the browser.</param>
    /// <returns>The id and the code.</returns>
    /// <exception cref="LiteRejectedException">Something is wrong or a limit is passed.</exception>
    public ShareCreated Create(ShareRequest request, IReadOnlyList<(string Path, byte[] Data, bool Demo)> files, string ip, string userAgent, string cookie)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(files);
        string title = Clean(request.Title, 80, false), author = Clean(request.Author, 60, false), description = Clean(request.Description, 500, true);
        if (title.Length == 0 || author.Length == 0) throw new LiteRejectedException("AMPL945", 400, "A share needs a title and an author.");
        if (!request.Rights) throw new LiteRejectedException("AMPL945", 400, "Confirm that you made this or may share it.");
        if (!TryDuration(request.Expires, out TimeSpan? duration)) throw new LiteRejectedException("AMPL945", 400, "The duration is one of 1h, 24h, 1w, 1m, never.");
        if (!files.Any(f => string.Equals(f.Path, request.Entry, StringComparison.Ordinal))) throw new LiteRejectedException("AMPL945", 400, "The song to build is not among the files.");
        List<ShareFile> listed = [.. files.Select(f => new ShareFile(f.Path, LiteBlobs.Hash(f.Data), f.Data.LongLength, f.Demo))];
        long own = listed.Where(f => !f.Demo).Select(f => f.Sha256).Distinct(StringComparer.Ordinal).Sum(h => listed.First(f => f.Sha256 == h).Size);
        DateTime stamp = now();
        lock (sync)
        {
            long add = listed.Where(f => !f.Demo && !blobSizes.ContainsKey(f.Sha256)).Select(f => f.Sha256).Distinct(StringComparer.Ordinal).Sum(h => listed.First(f => f.Sha256 == h).Size);
            if (blobSizes.Values.Sum() + add > limits.CeilingBytes) throw new LiteRejectedException("AMPL946", 503, "Sharing is not available for a moment: the store is full. Try again later.");
            ChargeIp(IpKey(ip), own, stamp);
            foreach ((string path, byte[] data, bool demo) in files)
            {
                string hash = LiteBlobs.Hash(data);
                if (demo || blobSizes.ContainsKey(hash)) continue;
                string target = Path.Combine(blobs, hash);
                string temp = target + ".tmp";
                File.WriteAllBytes(temp, data);
                File.Move(temp, target, true);
                blobSizes[hash] = data.LongLength;
            }

            string id;
            do id = RandomText(8); while (shares.ContainsKey(id));
            string code = RandomText(26);
            ShareRecord record = new() { Id = id, Title = title, Author = author, Description = description, Created = stamp, Expires = duration is null ? null : stamp + duration, Entry = request.Entry, Files = listed };
            SharePrivate secret = new() { Ip = ip, UserAgent = userAgent, Cookie = cookie, CodeHash = HashCode(code), Changes = [$"{stamp:O} created"] };
            Save(record, secret);
            shares[id] = record;
            secrets[id] = secret;
            dirty = true;
            return new ShareCreated(id, code, record.Expires);
        }
    }

    /// <summary>True when the text is a share id: 8 characters of Crockford base32 in upper case.</summary>
    /// <param name="id">The text.</param>
    /// <returns>True for a valid id.</returns>
    public static bool IsId(string? id) => id is { Length: 8 } && id.All(c => IdAlphabet.Contains(c, StringComparison.Ordinal));

    /// <summary>Puts in (or replaces) a permanent share of the owner under a chosen id: no expiry, never pushed out by capacity, no management code, not counted per address. Only the ceiling of the store holds.</summary>
    /// <param name="id">The id (see <see cref="IsId"/>).</param>
    /// <param name="title">The title.</param>
    /// <param name="author">The author.</param>
    /// <param name="description">The description.</param>
    /// <param name="entry">The song to build.</param>
    /// <param name="files">The files: path and bytes.</param>
    /// <returns>The stored share.</returns>
    /// <exception cref="LiteRejectedException">Something is wrong, the id belongs to an ordinary share, or the store is full.</exception>
    public ShareRecord PutPermanent(string id, string title, string author, string description, string entry, IReadOnlyList<(string Path, byte[] Data)> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (!IsId(id)) throw new LiteRejectedException("AMPL945", 400, "The id is 8 characters of 0-9 A-H J K M N P-T V-Z.");
        string cleanTitle = Clean(title, 80, false), cleanAuthor = Clean(author, 60, false), cleanDescription = Clean(description, 500, true);
        if (cleanTitle.Length == 0 || cleanAuthor.Length == 0) throw new LiteRejectedException("AMPL945", 400, "A share needs a title and an author.");
        if (!files.Any(f => string.Equals(f.Path, entry, StringComparison.Ordinal))) throw new LiteRejectedException("AMPL945", 400, "The song to build is not among the files.");
        List<ShareFile> listed = [.. files.Select(f => new ShareFile(f.Path, LiteBlobs.Hash(f.Data), f.Data.LongLength, false))];
        DateTime stamp = now();
        lock (sync)
        {
            if (shares.TryGetValue(id, out ShareRecord? existing) && !existing.Permanent) throw new LiteRejectedException("AMPL945", 409, "That id belongs to an ordinary share.");
            long add = listed.Where(f => !blobSizes.ContainsKey(f.Sha256)).DistinctBy(f => f.Sha256).Sum(f => f.Size);
            if (blobSizes.Values.Sum() + add > limits.CeilingBytes) throw new LiteRejectedException("AMPL946", 503, "The store is full.");
            foreach ((string _, byte[] data) in files)
            {
                string hash = LiteBlobs.Hash(data);
                if (blobSizes.ContainsKey(hash)) continue;
                string target = Path.Combine(blobs, hash);
                File.WriteAllBytes(target + ".tmp", data);
                File.Move(target + ".tmp", target, true);
                blobSizes[hash] = data.LongLength;
            }

            ShareRecord record = new() { Id = id, Title = cleanTitle, Author = cleanAuthor, Description = cleanDescription, Created = existing?.Created ?? stamp, Expires = null, Entry = entry, Files = listed, Permanent = true };
            WriteAtomic(Path.Combine(manifests, id + ".json"), JsonSerializer.Serialize(record, Json));
            shares[id] = record;
            dirty = true;
            return record;
        }
    }

    /// <summary>Finds a share and says whether it can be opened.</summary>
    /// <param name="id">The short id.</param>
    /// <returns>The share and its state (<c>available</c>, <c>expired</c>, <c>capacity</c> or <c>removed</c>), or null when the id is not known.</returns>
    public (ShareRecord Share, string State)? Find(string id)
    {
        lock (sync)
        {
            if (!shares.TryGetValue(id ?? string.Empty, out ShareRecord? record)) return null;
            Recompute();
            return (record, StateOf(record));
        }
    }

    /// <summary>The content of a file of an available share (not a demo file: those are the server's own).</summary>
    /// <param name="id">The short id.</param>
    /// <param name="hash">The SHA-256 of the file.</param>
    /// <param name="data">The bytes.</param>
    /// <returns>True when the share is available and lists that file.</returns>
    public bool TryOpenFile(string id, string hash, out byte[] data)
    {
        data = [];
        lock (sync)
        {
            if (!shares.TryGetValue(id ?? string.Empty, out ShareRecord? record)) return false;
            Recompute();
            if (StateOf(record) != "available" || !record.Files.Any(f => !f.Demo && string.Equals(f.Sha256, hash, StringComparison.Ordinal))) return false;
            return TryRead(hash, out data);
        }
    }

    /// <summary>True when an available share holds a file with this content.</summary>
    /// <param name="hash">The SHA-256.</param>
    /// <returns>True when found.</returns>
    public bool HasAvailableFile(string hash)
    {
        lock (sync)
        {
            Recompute();
            return shares.Values.Any(s => StateOf(s) == "available" && s.Files.Any(f => !f.Demo && f.Sha256 == hash));
        }
    }

    /// <summary>True when an available share holds a file with this content; its bytes are given (a build of a viewer may name the file by hash and need not send it).</summary>
    /// <param name="hash">The SHA-256.</param>
    /// <param name="data">The bytes.</param>
    /// <returns>True when found.</returns>
    public bool TryOpenAny(string hash, out byte[] data)
    {
        data = [];
        if (!LiteBlobs.IsHash(hash)) return false;
        lock (sync)
        {
            Recompute();
            return shares.Values.Any(s => StateOf(s) == "available" && s.Files.Any(f => !f.Demo && f.Sha256 == hash)) && TryRead(hash, out data);
        }
    }

    /// <summary>What the maker can do with the management code: look (<c>info</c>), change the duration (<c>expires</c>, counted from now) or take it down (<c>delete</c>: it becomes unavailable, the files stay).</summary>
    /// <param name="id">The short id.</param>
    /// <param name="code">The code of the maker.</param>
    /// <param name="action">info, expires or delete.</param>
    /// <param name="expires">The new duration name for <c>expires</c>.</param>
    /// <returns>The share and its state after the action.</returns>
    /// <exception cref="LiteRejectedException">Unknown id, a wrong code, an unknown action, or a share that is no longer available.</exception>
    public (ShareRecord Share, string State) Manage(string id, string code, string action, string? expires)
    {
        lock (sync)
        {
            if (!shares.TryGetValue(id ?? string.Empty, out ShareRecord? record) || !secrets.TryGetValue(id!, out SharePrivate? secret)) throw new LiteRejectedException("AMPL947", 404, "This share does not exist.");
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(HashCode(code ?? string.Empty)), Encoding.ASCII.GetBytes(secret.CodeHash))) throw new LiteRejectedException("AMPL947", 403, "That code does not belong to this share.");
            Recompute();
            string state = StateOf(record);
            if (state != "available") throw new LiteRejectedException("AMPL948", 410, "This share is no longer available.");
            DateTime stamp = now();
            if (action == "expires")
            {
                if (!TryDuration(expires, out TimeSpan? duration)) throw new LiteRejectedException("AMPL945", 400, "The duration is one of 1h, 24h, 1w, 1m, never.");
                record.Expires = duration is null ? null : stamp + duration;
                secret.Changes.Add($"{stamp:O} duration {expires}");
            }
            else if (action == "delete")
            {
                record.Removed = true;
                secret.Changes.Add($"{stamp:O} taken down by the maker");
            }
            else if (action != "info")
            {
                throw new LiteRejectedException("AMPL945", 400, "The action is info, expires or delete.");
            }

            if (action != "info")
            {
                Save(record, secret);
                dirty = true;
                Recompute();
            }

            return (record, StateOf(record));
        }
    }

    /// <summary>Takes a share down on a complaint (the owner's way: it becomes unavailable, the files stay so that it can be put back).</summary>
    /// <param name="id">The short id.</param>
    /// <param name="removed">True to take it down, false to put it back.</param>
    /// <returns>True when the share exists.</returns>
    public bool SetRemoved(string id, bool removed)
    {
        lock (sync)
        {
            if (!shares.TryGetValue(id, out ShareRecord? record)) return false;
            if (!secrets.TryGetValue(id, out SharePrivate? secret))
            {
                if (!record.Permanent) return false;
                record.Removed = removed;
                WriteAtomic(Path.Combine(manifests, id + ".json"), JsonSerializer.Serialize(record, Json));
                dirty = true;
                return true;
            }

            record.Removed = removed;
            secret.Changes.Add($"{now():O} {(removed ? "taken down" : "put back")} by the owner");
            Save(record, secret);
            dirty = true;
            return true;
        }
    }

    /// <summary>Ages what is kept about the makers (the address, the browser, the cookie id) and says which shares a cleanup would remove. Meant to run once an hour.</summary>
    public void Maintain()
    {
        lock (sync)
        {
            DateTime stamp = now();
            foreach ((string id, SharePrivate secret) in secrets)
            {
                if (!shares.TryGetValue(id, out ShareRecord? record)) continue;
                double days = (stamp - record.Created).TotalDays;
                int stage = days >= 30 ? 2 : days >= 14 ? 1 : 0;
                if (stage > secret.Stage)
                {
                    secret.Ip = AgeIp(secret.Ip, stage);
                    if (stage >= 1) secret.UserAgent = AgeUserAgent(secret.UserAgent);
                    if (stage >= 2) secret.Cookie = null;
                    secret.Stage = stage;
                    secret.Changes.Add($"{stamp:O} aged to stage {stage}");
                    Save(record, secret);
                }
            }

            dirty = true;
            Recompute();
        }
    }

    /// <summary>The state of a share now: available, expired, capacity (it does not fit in the store any more) or removed.</summary>
    private string StateOf(ShareRecord record)
    {
        if (record.Removed) return "removed";
        if (record.Permanent) return "available";
        if (record.Expires is { } end && end <= now()) return "expired";
        return excess.Contains(record.Id) ? "capacity" : "available";
    }

    /// <summary>Works out which shares do not fit in the store (newest first are kept, the oldest go), and writes the log line of every share that a cleanup would remove (once).</summary>
    private void Recompute()
    {
        DateTime stamp = now();
        if (!dirty && (stamp - computed).TotalSeconds < 30) return;
        // permanent shares are counted first (they always fit), the ordinary ones share what is left
        HashSet<string> counted = new(shares.Values.Where(s => s.Permanent && !s.Removed).SelectMany(s => s.Files).Select(f => f.Sha256), StringComparer.Ordinal);
        long total = shares.Values.Where(s => s.Permanent && !s.Removed).SelectMany(s => s.Files).DistinctBy(f => f.Sha256).Sum(f => f.Size);
        List<ShareRecord> alive = [.. shares.Values.Where(s => !s.Permanent && !s.Removed && (s.Expires is null || s.Expires > stamp)).OrderByDescending(s => s.Created)];
        HashSet<string> over = new(StringComparer.Ordinal);
        foreach (ShareRecord share in alive)
        {
            List<ShareFile> fresh = [.. share.Files.Where(f => !f.Demo && !counted.Contains(f.Sha256)).DistinctBy(f => f.Sha256)];
            long add = fresh.Sum(f => f.Size);
            if (total + add <= limits.StoreBytes)
            {
                total += add;
                foreach (ShareFile f in fresh) counted.Add(f.Sha256);
            }
            else
            {
                over.Add(share.Id);
            }
        }

        excess = over;
        computed = stamp;
        dirty = false;
        foreach (ShareRecord share in shares.Values)
        {
            if (share.Removed || share.Permanent || !secrets.TryGetValue(share.Id, out SharePrivate? secret)) continue;
            string? reason = share.Expires is { } end && end <= stamp ? "expired" : over.Contains(share.Id) ? "capacity" : null;
            if (reason is null || secret.Logged == reason) continue;
            long bytes = share.Files.Where(f => !f.Demo).DistinctBy(f => f.Sha256).Where(f => !shares.Values.Any(o => o.Id != share.Id && StateOf(o) == "available" && o.Files.Any(x => x.Sha256 == f.Sha256))).Sum(f => f.Size);
            File.AppendAllText(logFile, string.Join('\t', stamp.ToString("O", CultureInfo.InvariantCulture), share.Id, "would remove", reason, bytes.ToString(CultureInfo.InvariantCulture), ((int)(stamp - share.Created).TotalDays).ToString(CultureInfo.InvariantCulture) + " days old") + Environment.NewLine);
            secret.Logged = reason;
            Save(share, secret);
        }
    }

    /// <summary>Counts the bytes of own files that an address put in shares in the last hour; a limit that is passed says in how many seconds there is room again.</summary>
    private void ChargeIp(string key, long bytes, DateTime stamp)
    {
        if (!hourly.TryGetValue(key, out List<(DateTime At, long Bytes)>? list)) hourly[key] = list = [];
        list.RemoveAll(e => e.At <= stamp.AddHours(-1));
        long used = list.Sum(e => e.Bytes);
        if (used + bytes > limits.IpBytesPerHour)
        {
            if (bytes > limits.IpBytesPerHour) throw new LiteRejectedException("AMPL944", 413, $"These files are {bytes / 1024 / 1024} MB; one address may share at most {limits.IpBytesPerHour / 1024 / 1024} MB of its own files in an hour.");
            long over = used + bytes - limits.IpBytesPerHour, freed = 0;
            int wait = 60;
            foreach ((DateTime at, long size) in list.OrderBy(e => e.At))
            {
                freed += size;
                if (freed >= over)
                {
                    wait = Math.Max(1, (int)Math.Ceiling((at.AddHours(1) - stamp).TotalSeconds));
                    break;
                }
            }

            throw new LiteRejectedException("AMPL944", 429, $"You have shared {used / 1024 / 1024} MB of your own files in the last hour (the most is {limits.IpBytesPerHour / 1024 / 1024} MB). Try again in {(wait + 59) / 60} minutes.") { RetryAfterSeconds = wait };
        }

        if (bytes > 0) list.Add((stamp, bytes));
    }

    private bool TryRead(string hash, out byte[] data)
    {
        data = [];
        try
        {
            data = File.ReadAllBytes(Path.Combine(blobs, hash));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void Save(ShareRecord record, SharePrivate secret)
    {
        WriteAtomic(Path.Combine(manifests, record.Id + ".json"), JsonSerializer.Serialize(record, Json));
        WriteAtomic(Path.Combine(privates, record.Id + ".json"), JsonSerializer.Serialize(secret, Json));
    }

    private static void WriteAtomic(string path, string text)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, true);
    }

    private static string HashCode(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))).ToLowerInvariant();

    private static string RandomText(int length)
    {
        StringBuilder text = new(length);
        Span<byte> bytes = stackalloc byte[length];
        RandomNumberGenerator.Fill(bytes);
        foreach (byte b in bytes) text.Append(IdAlphabet[b & 31]);
        return text.ToString();
    }

    /// <summary>Plain text: trimmed, no control characters (a description may have line breaks), at most a length, and never longer than it says.</summary>
    private static string Clean(string? text, int max, bool lines)
    {
        string trimmed = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (trimmed.Length > max) throw new LiteRejectedException("AMPL945", 400, $"That is longer than {max} characters.");
        if (trimmed.Any(c => char.IsControl(c) && !(lines && c == '\n'))) throw new LiteRejectedException("AMPL945", 400, "Text only: no control characters.");
        return trimmed;
    }
}
