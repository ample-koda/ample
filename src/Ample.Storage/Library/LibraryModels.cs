namespace Ample.Storage.Library;

/// <summary>What kind of thing an asset is (decided from its file name; <see cref="Other"/> files are listed but never read).</summary>
public enum AssetKind
{
    /// <summary>Audio sample (wav, flac, mp3, ogg, aiff).</summary>
    Sample,

    /// <summary>A pack (<c>pack.json</c>).</summary>
    Pack,

    /// <summary>A voice (<c>*.voice.json</c>).</summary>
    Voice,

    /// <summary>A plugin (<c>plugin.json</c>).</summary>
    Plugin,

    /// <summary>Lyrics (<c>.amplelrc</c>, <c>.lyr</c>, <c>.lrc</c>).</summary>
    Lyrics,

    /// <summary>Chords (<c>.chords</c>).</summary>
    Chords,

    /// <summary>A reusable module (<c>*.module</c>).</summary>
    Module,

    /// <summary>A block template (<c>*.template.json</c>).</summary>
    Template,

    /// <summary>Data (<c>.json</c>, <c>.csv</c>).</summary>
    Data,

    /// <summary>Anything else: recorded by name and size, never opened.</summary>
    Other,
}

/// <summary>A linked folder (catalog). <see cref="State"/> is worked out when it is listed: online, offline or other (a different catalog is at that path).</summary>
/// <param name="Id">Id in this user's library.</param>
/// <param name="Uuid">Identity of the catalog, the same on every computer (written to a marker file when the link is writable).</param>
/// <param name="Name">Display name.</param>
/// <param name="Path">Folder on this computer.</param>
/// <param name="Scope"><c>global</c> (every project) or <c>project</c>.</param>
/// <param name="ProjectId">The project, for project links.</param>
/// <param name="ReadOnly">True: AMPLE never writes into it.</param>
/// <param name="State">online, offline or other.</param>
/// <param name="Fingerprint">Identity by content, for read-only folders that cannot carry a marker.</param>
/// <param name="Added">When it was linked.</param>
public sealed record Mount(string Id, string Uuid, string Name, string Path, string Scope, string? ProjectId, bool ReadOnly, string State, string Fingerprint, DateTimeOffset Added);

/// <summary>One asset of the catalog.</summary>
/// <param name="Uuid">Stable identity; survives renames and moves.</param>
/// <param name="Kind">Kind.</param>
/// <param name="Name">Name as the song writes it (the relative path without extension, or the pack name and version).</param>
/// <param name="Tags">Tags, space separated.</param>
/// <param name="Hash">Content hash (<c>sha256:</c>...), or null for kinds that are never read.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="MountId">The mount it lives in.</param>
/// <param name="RelPath">Path inside the mount, with <c>/</c>.</param>
/// <param name="License">License text, if known.</param>
/// <param name="Credit">Credit text, if known.</param>
/// <param name="Added">When it was first seen.</param>
/// <param name="Revision">Grows when the content changed (the uuid stays).</param>
/// <param name="Missing">True when the last scan did not find it.</param>
public sealed record AssetRecord(string Uuid, AssetKind Kind, string Name, string Tags, string? Hash, long Size, string MountId, string RelPath, string License, string Credit, DateTimeOffset Added, int Revision, bool Missing);

/// <summary>What a scan did.</summary>
/// <param name="Added">New assets.</param>
/// <param name="Changed">Assets whose content changed (new revision).</param>
/// <param name="Moved">Assets that were found again under another path (same uuid).</param>
/// <param name="Missing">Assets that were not found.</param>
/// <param name="Unchanged">Assets that were left as they were (size and time unchanged).</param>
/// <param name="Hashed">Files that were read for hashing.</param>
/// <param name="Milliseconds">How long it took.</param>
public sealed record ScanReport(int Added, int Changed, int Moved, int Missing, int Unchanged, int Hashed, long Milliseconds);

/// <summary>A reference to an asset as a song or a lock file holds it.</summary>
/// <param name="Name">Name or relative path as written.</param>
/// <param name="Uuid">Asset uuid, if known.</param>
/// <param name="Hash">Hash it was pinned with, if any.</param>
public sealed record AssetRef(string Name, string? Uuid = null, string? Hash = null);

/// <summary>The answer of the resolver.</summary>
/// <param name="Status">found, changed (found but another hash than pinned), offline (its folder is not reachable) or missing.</param>
/// <param name="Where">project, mount:&lt;id&gt; or pack.</param>
/// <param name="FullPath">Where the file is.</param>
/// <param name="Asset">The catalog record, if there is one.</param>
/// <param name="Code">Diagnostic code for the problems (AMP5290, AMP5291, AMP5294).</param>
/// <param name="Message">One plain sentence.</param>
public sealed record Resolution(string Status, string? Where, string? FullPath, AssetRecord? Asset, string? Code, string? Message);

/// <summary>Where an asset is used in a song.</summary>
/// <param name="AssetUuid">The asset.</param>
/// <param name="Block">The block.</param>
/// <param name="Parameter">The parameter that names it.</param>
/// <param name="Line">0-based line of the parameter.</param>
public sealed record AssetUse(string AssetUuid, string Block, string Parameter, int Line);

/// <summary>A pack manifest (<c>pack.json</c>), validated.</summary>
/// <param name="Name">Pack name.</param>
/// <param name="Version">Pack version.</param>
/// <param name="License">License text.</param>
/// <param name="Format">Format, for example sfz.</param>
public sealed record PackManifest(string Name, string Version, string License, string Format);
