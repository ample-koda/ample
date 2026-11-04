using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ample.Server;

/// <summary>
/// The optional login of <c>ample serve --login</c>: one fixed user and password (kept as a PBKDF2 hash in the AMPLE user data folder)
/// that trade for a session token, accepted wherever the launch token is. Without <c>--login</c> none of this runs.
/// </summary>
public sealed class AuthService
{
    private const int Iterations = 210_000;
    private const int MaxFailures = 5;
    private static readonly TimeSpan SessionLife = TimeSpan.FromHours(12);
    private static readonly TimeSpan LockLife = TimeSpan.FromMinutes(1);

    private readonly string user;
    private readonly byte[] salt;
    private readonly byte[] hash;
    private readonly int iterations;
    private readonly ConcurrentDictionary<string, DateTime> sessions = new();
    private readonly ConcurrentDictionary<string, (int Failures, DateTime LockedUntil)> throttle = new();

    private AuthService(string user, byte[] salt, byte[] hash, int iterations)
    {
        this.user = user;
        this.salt = salt;
        this.hash = hash;
        this.iterations = iterations;
    }

    /// <summary>Gets the file that holds the user name and the password hash.</summary>
    /// <param name="home">The AMPLE user data folder.</param>
    public static string FilePath(string home) => Path.Combine(home, "auth.json");

    /// <summary>Stores the user name and the salted hash of the password (never the password itself).</summary>
    /// <param name="home">The AMPLE user data folder.</param>
    /// <param name="user">The user name.</param>
    /// <param name="password">The password.</param>
    public static void SetPassword(string home, string user, string password)
    {
        if (string.IsNullOrWhiteSpace(user) || password.Length < 8)
        {
            throw new ArgumentException("A user name and a password of at least 8 characters are required.");
        }

        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        Directory.CreateDirectory(home);
        File.WriteAllText(FilePath(home), JsonSerializer.Serialize(new { user = user.Trim(), salt = Convert.ToBase64String(salt), hash = Convert.ToBase64String(hash), iterations = Iterations }));
    }

    /// <summary>Loads the stored credentials; null when none were set yet.</summary>
    /// <param name="home">The AMPLE user data folder.</param>
    public static AuthService? Load(string home)
    {
        string path = FilePath(home);
        if (!File.Exists(path))
        {
            return null;
        }

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement r = doc.RootElement;
        return new AuthService(r.GetProperty("user").GetString()!, Convert.FromBase64String(r.GetProperty("salt").GetString()!), Convert.FromBase64String(r.GetProperty("hash").GetString()!), r.GetProperty("iterations").GetInt32());
    }

    /// <summary>Checks the credentials and, when they match, returns a new session token; null when wrong or locked out.</summary>
    /// <param name="client">The caller's address (for the lockout).</param>
    /// <param name="name">The user name given.</param>
    /// <param name="password">The password given.</param>
    /// <param name="locked">True when the caller is locked out for a minute after too many failures.</param>
    public string? TryLogin(string client, string name, string password, out bool locked)
    {
        locked = false;
        DateTime now = DateTime.UtcNow;
        if (throttle.TryGetValue(client, out (int Failures, DateTime LockedUntil) t) && t.LockedUntil > now)
        {
            locked = true;
            return null;
        }

        // the hash is always computed, so a wrong user name takes as long as a wrong password
        byte[] given = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, hash.Length);
        bool ok = CryptographicOperations.FixedTimeEquals(given, hash) & CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(name.Trim()), Encoding.UTF8.GetBytes(user));
        if (!ok)
        {
            int failures = (throttle.TryGetValue(client, out (int Failures, DateTime LockedUntil) p) ? p.Failures : 0) + 1;
            throttle[client] = failures >= MaxFailures ? (0, now + LockLife) : (failures, DateTime.MinValue);
            return null;
        }

        throttle.TryRemove(client, out _);
        foreach (KeyValuePair<string, DateTime> old in sessions.Where(s => s.Value < now).ToArray())
        {
            sessions.TryRemove(old.Key, out _);
        }

        string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        sessions[token] = now + SessionLife;
        return token;
    }

    /// <summary>Gets whether the token is a live session token.</summary>
    /// <param name="token">The token given.</param>
    public bool IsSession(string token) => sessions.TryGetValue(token, out DateTime until) && until > DateTime.UtcNow;

    /// <summary>Ends a session.</summary>
    /// <param name="token">The token to end.</param>
    public void Logout(string token) => sessions.TryRemove(token, out _);

    /// <summary>Gets the sign-in page (plain HTML with the AMPLE logo; it warns when the connection is not encrypted).</summary>
    public static string LoginPage => """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>AMPLE sign in</title>
        <link rel="icon" href="/assets/icon/ample.svg" type="image/svg+xml">
        <style>
        *{box-sizing:border-box}
        body{margin:0;min-height:100vh;display:grid;place-items:center;font:15px system-ui,-apple-system,"Segoe UI",sans-serif;color:#eef1f6;background:#0b0e14;overflow:hidden}
        .glow{position:fixed;inset:-20%;z-index:0;filter:blur(90px);opacity:.55}
        .glow i{position:absolute;border-radius:50%;width:46vmax;height:46vmax}
        .glow i:nth-child(1){background:#2de2ff;left:-6%;top:-10%;animation:a 22s ease-in-out infinite alternate}
        .glow i:nth-child(2){background:#ff4fd8;right:-8%;bottom:-14%;animation:a 26s ease-in-out infinite alternate-reverse}
        .glow i:nth-child(3){background:#6a5cff;left:30%;top:35%;width:30vmax;height:30vmax;opacity:.6;animation:a 30s ease-in-out infinite alternate}
        @keyframes a{to{transform:translate(9vmax,6vmax) scale(1.15)}}
        .eq{position:fixed;left:0;right:0;bottom:0;height:22vh;display:flex;align-items:flex-end;gap:6px;padding:0 2vw;z-index:0;opacity:.18}
        .eq b{flex:1;border-radius:4px 4px 0 0;background:linear-gradient(#2de2ff,#ff4fd8);height:20%;animation:e 1.6s ease-in-out infinite alternate}
        @keyframes e{to{height:95%}}
        main{position:relative;z-index:1;width:min(380px,92vw);display:grid;gap:18px;justify-items:center}
        .logo{width:min(300px,80vw);height:auto;filter:drop-shadow(0 0 18px #ff4fd855) drop-shadow(0 0 10px #2de2ff55);animation:f 6s ease-in-out infinite alternate}
        @keyframes f{to{transform:translateY(-6px)}}
        .tag{margin:-8px 0 0;font-size:12px;letter-spacing:.28em;text-transform:uppercase;color:#9fb0c8}
        form{width:100%;display:grid;gap:12px;padding:26px;border-radius:16px;background:#141a2499;backdrop-filter:blur(14px) saturate(140%);-webkit-backdrop-filter:blur(14px) saturate(140%);border:1px solid #ffffff1f;box-shadow:0 20px 60px #0008,inset 0 1px 0 #ffffff14}
        input{font:inherit;color:inherit;padding:12px 14px;border-radius:10px;border:1px solid #ffffff22;background:#0b0e1488;outline:none;transition:border-color .2s,box-shadow .2s}
        input:focus{border-color:#2de2ff;box-shadow:0 0 0 3px #2de2ff33,0 0 18px #2de2ff44}
        button{font:inherit;font-weight:700;letter-spacing:.04em;color:#07101a;padding:12px;border:0;border-radius:10px;cursor:pointer;background:linear-gradient(90deg,#2de2ff,#ff4fd8);box-shadow:0 6px 24px #ff4fd844;transition:transform .15s,box-shadow .15s,filter .15s}
        button:hover{transform:translateY(-1px);box-shadow:0 10px 30px #2de2ff55;filter:brightness(1.08)}
        button:disabled{opacity:.6;cursor:wait}
        #m{min-height:1.2em;font-size:13px;color:#ff8fb1;text-align:center}
        .w{font-size:12px;line-height:1.4;color:#ffd08a;background:#ffb86b14;border:1px solid #ffb86b40;border-radius:8px;padding:8px 10px}
        .shake{animation:s .35s}
        @keyframes s{20%,60%{transform:translateX(-7px)}40%,80%{transform:translateX(7px)}}
        @media (prefers-reduced-motion:reduce){.glow i,.logo,.shake{animation:none!important}}   /* the big drifting and shaking stops, the small equalizer keeps moving: it is the page's signature and Windows turns animations off by itself in a remote desktop session */
        </style></head>
        <body><div class="glow"><i></i><i></i><i></i></div><div class="eq" id="eq" aria-hidden="true"></div>
        <main><img class="logo" src="/assets/logo/ample-logo.svg" alt="AMPLE"><p class="tag">Assisted Music Programming</p>
        <form id="f"><input id="u" placeholder="User" autocomplete="username" required autofocus><input id="p" type="password" placeholder="Password" autocomplete="current-password" required><input id="t" name="website" tabindex="-1" autocomplete="off" aria-hidden="true" style="position:absolute;left:-9999px;width:1px;height:1px;opacity:0"><input type="hidden" id="s" name="stamp"><button id="b">Sign in</button><div id="m" role="status"></div>
        <div class="w" id="w" hidden>This connection is not encrypted (http): your password travels in the clear. Use it only through an encrypted tunnel (SSH, VPN).</div></form></main>
        <script>
        const eq=document.getElementById('eq');for(let i=0;i<48;i++){const b=document.createElement('b');b.style.animationDuration=(0.9+Math.random()*1.8).toFixed(2)+'s';b.style.animationDelay=(-Math.random()*2).toFixed(2)+'s';eq.append(b);}
        const h=location.hostname;if(location.protocol==='http:'&&h!=='localhost'&&h!=='127.0.0.1'&&h!=='[::1]')document.getElementById('w').hidden=false;
        document.getElementById('s').value=Date.now();const f=document.getElementById('f'),m=document.getElementById('m'),b=document.getElementById('b');
        f.onsubmit=async e=>{e.preventDefault();m.textContent='';b.disabled=true;
        try{const r=await fetch('/api/login',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({user:u.value,password:p.value,website:t.value,stamp:Number(s.value)})});
        const j=await r.json().catch(()=>({}));if(r.ok&&j.token){location.replace((new URLSearchParams(location.search).get('next')==='/remix/'?'/remix/':'/')+'#token='+j.token);return;}
        m.textContent=r.status===429?'Too many attempts. Wait a minute.':'Wrong user or password.';f.classList.remove('shake');void f.offsetWidth;f.classList.add('shake');}
        catch{m.textContent='Could not reach the server.';}b.disabled=false;};
        </script></body></html>
        """;
}
