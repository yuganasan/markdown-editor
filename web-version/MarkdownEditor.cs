// MarkdownEditor.exe - native launcher for markdown-editor.html
//
// Opens the editor in an Edge app window (no address bar, no tab strip) and
// gives the page real file access over a loopback socket that only this
// process can talk to. Exits when the window goes away.
//
// Build with build.ps1 (uses the C# compiler that ships with Windows).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class Program
{
    static string docPath = "";
    static bool hadBom = false;
    static string token;
    static int lastBeat;
    static bool sawClient = false;
    static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].Length > 0)
        {
            try { docPath = Path.GetFullPath(args[0]); }
            catch { docPath = ""; }
        }

        token = Guid.NewGuid().ToString("N");
        TcpListener listener;
        int port;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        catch (Exception ex)
        {
            Fail("Could not open a local port.\r\n\r\n" + ex.Message);
            return 1;
        }

        lastBeat = Environment.TickCount;
        Thread watch = new Thread(IdleWatch);
        watch.IsBackground = true;
        watch.Start();

        if (!Launch("http://127.0.0.1:" + port + "/"))
        {
            Fail("Could not start a browser window.");
            return 1;
        }

        while (true)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch { break; }
            ThreadPool.QueueUserWorkItem(Handle, client);
        }
        return 0;
    }

    // ---------------------------------------------------------------- lifetime

    static void Beat()
    {
        lastBeat = Environment.TickCount;
        sawClient = true;
    }

    static void IdleWatch()
    {
        while (true)
        {
            Thread.Sleep(2000);
            // generous grace period while the browser is still starting up
            int limit = sawClient ? 20000 : 90000;
            if (Environment.TickCount - lastBeat > limit) Environment.Exit(0);
        }
    }

    static void Fail(string msg)
    {
        MessageBox.Show(msg, "Markdown Editor", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    // ---------------------------------------------------------------- browser

    static bool Launch(string url)
    {
        string[] candidates = new string[] {
            Environment.GetEnvironmentVariable("ProgramFiles(x86)") + @"\Microsoft\Edge\Application\msedge.exe",
            Environment.GetEnvironmentVariable("ProgramFiles") + @"\Microsoft\Edge\Application\msedge.exe",
            Environment.GetEnvironmentVariable("ProgramFiles") + @"\Google\Chrome\Application\chrome.exe",
            Environment.GetEnvironmentVariable("ProgramFiles(x86)") + @"\Google\Chrome\Application\chrome.exe",
            Environment.GetEnvironmentVariable("LOCALAPPDATA") + @"\Google\Chrome\Application\chrome.exe"
        };
        foreach (string exe in candidates)
        {
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) continue;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe,
                    "--app=" + url + " --window-size=1280,880");
                psi.UseShellExecute = false;
                Process.Start(psi);
                return true;
            }
            catch { }
        }
        // no Chromium browser found: fall back to whatever handles http
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(url);
            psi.UseShellExecute = true;
            Process.Start(psi);
            return true;
        }
        catch { return false; }
    }

    // ---------------------------------------------------------------- server

    static void Handle(object state)
    {
        TcpClient client = (TcpClient)state;
        try
        {
            using (client)
            {
                client.ReceiveTimeout = 20000;
                client.SendTimeout = 20000;
                NetworkStream ns = client.GetStream();

                MemoryStream buf = new MemoryStream();
                byte[] chunk = new byte[8192];
                int headEnd = -1;
                while (headEnd < 0)
                {
                    int n = ns.Read(chunk, 0, chunk.Length);
                    if (n <= 0) return;
                    buf.Write(chunk, 0, n);
                    headEnd = FindHeaderEnd(buf.GetBuffer(), (int)buf.Length);
                    if (headEnd < 0 && buf.Length > 65536) return;
                }

                byte[] all = buf.GetBuffer();
                string head = Encoding.UTF8.GetString(all, 0, headEnd);
                string[] lines = head.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                string[] req = lines[0].Split(' ');
                if (req.Length < 2) return;
                string method = req[0];
                string target = req[1];

                Dictionary<string, string> hdr = new Dictionary<string, string>();
                for (int i = 1; i < lines.Length; i++)
                {
                    int c = lines[i].IndexOf(':');
                    if (c > 0) hdr[lines[i].Substring(0, c).Trim().ToLowerInvariant()]
                                 = lines[i].Substring(c + 1).Trim();
                }

                int len = 0;
                if (hdr.ContainsKey("content-length")) int.TryParse(hdr["content-length"], out len);
                byte[] body = new byte[len];
                if (len > 0)
                {
                    int have = Math.Min((int)buf.Length - (headEnd + 4), len);
                    if (have > 0) Array.Copy(all, headEnd + 4, body, 0, have);
                    while (have < len)
                    {
                        int n = ns.Read(body, have, len - have);
                        if (n <= 0) break;
                        have += n;
                    }
                }

                string path = target;
                string query = "";
                int q = target.IndexOf('?');
                if (q >= 0) { query = target.Substring(q + 1); path = target.Substring(0, q); }

                Route(ns, method, path, query, hdr, body);
            }
        }
        catch { }
    }

    static void Route(NetworkStream ns, string method, string path, string query,
                      Dictionary<string, string> hdr, byte[] body)
    {
        if (path == "/" || path == "/index.html")
        {
            Beat();
            Send(ns, 200, "text/html; charset=utf-8", Utf8NoBom.GetBytes(Page()), null);
            return;
        }

        // everything past this point touches the disk: require the token that
        // was handed to the page itself, so no other local page can drive it
        string tok = hdr.ContainsKey("x-token") ? hdr["x-token"] : "";
        if (tok != token)
        {
            Send(ns, 403, "text/plain", Utf8NoBom.GetBytes("forbidden"), null);
            return;
        }
        Beat();

        if (path == "/ping") { Send(ns, 200, "text/plain", Utf8NoBom.GetBytes("ok"), null); return; }

        if (path == "/bye")
        {
            Send(ns, 200, "text/plain", Utf8NoBom.GetBytes("bye"), null);
            Environment.Exit(0);
            return;
        }

        if (path == "/doc")
        {
            if (method == "PUT") SaveDoc(ns, body);
            else SendDoc(ns);
            return;
        }

        if (path == "/open")
        {
            string picked = Dialog(false, "");
            if (picked == null) { Send(ns, 204, "text/plain", new byte[0], null); return; }
            docPath = picked;
            SendDoc(ns);
            return;
        }

        if (path == "/saveas")
        {
            string suggested = QueryValue(query, "name");
            if (suggested.Length == 0) suggested = "untitled.md";
            string picked = Dialog(true, suggested);
            if (picked == null) { Send(ns, 204, "text/plain", new byte[0], null); return; }
            docPath = picked;
            Dictionary<string, string> extra = new Dictionary<string, string>();
            extra["X-Path"] = Uri.EscapeDataString(docPath);
            extra["X-Name"] = Uri.EscapeDataString(Path.GetFileName(docPath));
            Send(ns, 200, "text/plain", new byte[0], extra);
            return;
        }

        Send(ns, 404, "text/plain", Utf8NoBom.GetBytes("not found"), null);
    }

    // ---------------------------------------------------------------- files

    static void SendDoc(NetworkStream ns)
    {
        string text = "";
        string name = "untitled.md";
        try
        {
            if (docPath.Length > 0)
            {
                name = Path.GetFileName(docPath);
                if (File.Exists(docPath))
                {
                    byte[] raw = File.ReadAllBytes(docPath);
                    hadBom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
                    text = hadBom
                        ? new UTF8Encoding(false).GetString(raw, 3, raw.Length - 3)
                        : new UTF8Encoding(false).GetString(raw);
                }
            }
        }
        catch (Exception ex)
        {
            Send(ns, 500, "text/plain", Utf8NoBom.GetBytes(ex.Message), null);
            return;
        }
        Dictionary<string, string> extra = new Dictionary<string, string>();
        extra["X-Path"] = Uri.EscapeDataString(docPath);
        extra["X-Name"] = Uri.EscapeDataString(name);
        Send(ns, 200, "text/plain; charset=utf-8", Utf8NoBom.GetBytes(text), extra);
    }

    static void SaveDoc(NetworkStream ns, byte[] body)
    {
        if (docPath.Length == 0)
        {
            Send(ns, 409, "text/plain", Utf8NoBom.GetBytes("no file selected"), null);
            return;
        }
        try
        {
            // write beside the target, then swap in: a crash mid-save cannot
            // leave the original truncated
            string tmp = docPath + ".mdtmp";
            using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                if (hadBom) fs.Write(new byte[] { 0xEF, 0xBB, 0xBF }, 0, 3);
                fs.Write(body, 0, body.Length);
                fs.Flush();
            }
            if (File.Exists(docPath)) File.Replace(tmp, docPath, null);
            else File.Move(tmp, docPath);
            Send(ns, 200, "text/plain", Utf8NoBom.GetBytes("ok"), null);
        }
        catch (Exception ex)
        {
            Send(ns, 500, "text/plain", Utf8NoBom.GetBytes(ex.Message), null);
        }
    }

    const string Filter =
        "Markdown (*.md;*.markdown;*.mdown;*.mkd)|*.md;*.markdown;*.mdown;*.mkd|" +
        "Text (*.txt)|*.txt|All files (*.*)|*.*";

    static string Dialog(bool save, string suggested)
    {
        string result = null;
        Thread t = new Thread(delegate()
        {
            // a hidden topmost owner keeps the dialog in front of the app window
            using (Form owner = new Form())
            {
                owner.ShowInTaskbar = false;
                owner.TopMost = true;
                owner.StartPosition = FormStartPosition.CenterScreen;
                owner.Size = new Size(1, 1);
                owner.Opacity = 0;
                owner.Show();
                if (save)
                {
                    using (SaveFileDialog d = new SaveFileDialog())
                    {
                        d.Title = "Save markdown file";
                        d.Filter = Filter;
                        d.FileName = suggested;
                        d.DefaultExt = "md";
                        d.AddExtension = true;
                        if (docPath.Length > 0)
                        {
                            try { d.InitialDirectory = Path.GetDirectoryName(docPath); }
                            catch { }
                        }
                        if (d.ShowDialog(owner) == DialogResult.OK) result = d.FileName;
                    }
                }
                else
                {
                    using (OpenFileDialog d = new OpenFileDialog())
                    {
                        d.Title = "Open markdown file";
                        d.Filter = Filter;
                        d.CheckFileExists = true;
                        if (docPath.Length > 0)
                        {
                            try { d.InitialDirectory = Path.GetDirectoryName(docPath); }
                            catch { }
                        }
                        if (d.ShowDialog(owner) == DialogResult.OK) result = d.FileName;
                    }
                }
                owner.Close();
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result;
    }

    // ---------------------------------------------------------------- helpers

    static string Page()
    {
        string html = HtmlSource.Text;
        string inject = "<script>window.__HOST__={token:\"" + token + "\"};</script>";
        int i = html.IndexOf("<script>");
        if (i < 0) return html;
        return html.Substring(0, i) + inject + html.Substring(i);
    }

    static string QueryValue(string query, string key)
    {
        string[] pairs = query.Split('&');
        foreach (string p in pairs)
        {
            int eq = p.IndexOf('=');
            if (eq > 0 && p.Substring(0, eq) == key)
                return Uri.UnescapeDataString(p.Substring(eq + 1));
        }
        return "";
    }

    static int FindHeaderEnd(byte[] b, int len)
    {
        for (int i = 0; i + 3 < len; i++)
            if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10) return i;
        return -1;
    }

    static string Reason(int code)
    {
        switch (code)
        {
            case 200: return "OK";
            case 204: return "No Content";
            case 403: return "Forbidden";
            case 404: return "Not Found";
            case 409: return "Conflict";
            default: return "Internal Server Error";
        }
    }

    static void Send(NetworkStream ns, int code, string ctype, byte[] body,
                     Dictionary<string, string> extra)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(code).Append(' ').Append(Reason(code)).Append("\r\n");
        sb.Append("Content-Type: ").Append(ctype).Append("\r\n");
        sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        sb.Append("Cache-Control: no-store\r\n");
        sb.Append("X-Content-Type-Options: nosniff\r\n");
        sb.Append("Connection: close\r\n");
        if (extra != null)
            foreach (KeyValuePair<string, string> kv in extra)
                sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
        sb.Append("\r\n");
        byte[] h = Encoding.ASCII.GetBytes(sb.ToString());
        ns.Write(h, 0, h.Length);
        if (body.Length > 0) ns.Write(body, 0, body.Length);
        ns.Flush();
    }
}
