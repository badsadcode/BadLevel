using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using NoorLevel.Export;
using NoorLevel.Preview;

namespace NoorLevel.Previewer
{
    /// <summary>The NoorLevel previewer: a window around a WebView2 that shows wwwroot/index.html.
    /// The page calls https://noorlevel.app/api/... ; those requests never leave the app: they are
    /// answered here by <see cref="PreviewApi"/> (the same code `noorlevel serve` uses).</summary>
    public sealed class MainForm : Form
    {
        const string Origin = "https://noorlevel.app/";
        readonly WebView2 web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(21, 19, 26) };
        readonly PreviewApi api = new PreviewApi();
        readonly string www = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        CoreWebView2Environment env;

        public MainForm()
        {
            Text = "NoorLevel Previewer";
            ClientSize = new Size(1500, 920);
            MinimumSize = new Size(900, 600);
            BackColor = Color.FromArgb(21, 19, 26);
            StartPosition = FormStartPosition.CenterScreen;
            Controls.Add(web);
            Load += async (s, e) => await Init();
        }

        async Task Init()
        {
            try
            {
                var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoorLevel", "WebView2");
                env = await CoreWebView2Environment.CreateAsync(null, data);
                await web.EnsureCoreWebView2Async(env);
            }
            catch (WebView2RuntimeNotFoundException)
            {
                MessageBox.Show(this, "NoorLevel needs the Microsoft Edge WebView2 Runtime.\n\nIt comes with Windows 11 and current Windows 10; otherwise get the Evergreen runtime from https://developer.microsoft.com/microsoft-edge/webview2/",
                    "WebView2 Runtime missing", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
                return;
            }
            var core = web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = true;            // F12 for the curious
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;  // Ctrl+R / F5 don't reload the app
            core.AddWebResourceRequestedFilter(Origin + "*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnRequest;
            core.WebMessageReceived += OnMessage;
            core.DocumentTitleChanged += (s, e) => Text = core.DocumentTitle;
            core.Navigate(Origin + "index.html");
        }

        void OnRequest(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            var uri = new Uri(e.Request.Uri);
            string method = e.Request.Method, path = uri.AbsolutePath, body = null;
            if (e.Request.Content != null)
                using (var r = new StreamReader(e.Request.Content, Encoding.UTF8)) body = r.ReadToEnd();
            if (!path.StartsWith("/api/"))
            {
                e.Response = Respond(PreviewApi.StaticFile(www, path));
                return;
            }
            // generating takes a moment: do it off the UI thread so the page keeps animating
            var deferral = e.GetDeferral();
            var query = PreviewApi.ParseQuery(uri.Query);
            Task.Run(() => api.Handle(method, path, query, body)).ContinueWith(t =>
            {
                BeginInvoke(new Action(() =>
                {
                    try { e.Response = Respond(t.Result); }
                    finally { deferral.Complete(); }
                }));
            });
        }

        CoreWebView2WebResourceResponse Respond(ApiResponse r)
        {
            var headers = $"Content-Type: {r.ContentType}\r\nCache-Control: no-store\r\nAccess-Control-Allow-Origin: *";
            if (r.FileName != null) headers += $"\r\nContent-Disposition: attachment; filename=\"{r.FileName}\"";
            return env.CreateWebResourceResponse(new MemoryStream(r.Body), r.Status, r.Status == 200 ? "OK" : "Error", headers);
        }

        void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (!(Json.Parse(e.WebMessageAsJson) is Dictionary<string, object> msg) || !(msg.TryGetValue("type", out var t) && (string)t == "export")) return;
            int id = (int)(double)msg["id"];
            string format = (string)msg["format"];
            var level = api.Get(id);
            if (level == null) { Reply("That level is gone — generate again."); return; }
            byte[] data; string name;
            try { data = Exporters.Export(level, format, out name); }
            catch (Exception ex) { Reply("Export failed: " + ex.Message); return; }
            var fmt = Exporters.Formats.First(f => f.Id == format);
            using (var dlg = new SaveFileDialog
            {
                FileName = name,
                Filter = $"{fmt.Label} (*{Path.GetExtension(name)})|*{Path.GetExtension(name)}|All files (*.*)|*.*",
                Title = "Export " + fmt.Label,
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) { Reply(""); return; }
                File.WriteAllBytes(dlg.FileName, data);
                if (format == "quake" || format == "quake2" || format == "valve")
                    Reply($"Saved {Path.GetFileName(dlg.FileName)} ({data.Length / 1024.0:0.#} KB): unzip next to your map folder.");
                else Reply($"Saved {Path.GetFileName(dlg.FileName)} ({data.Length / 1024.0:0.#} KB)");
            }
        }

        void Reply(string text) =>
            web.CoreWebView2.PostWebMessageAsJson(Json.Write(new Dictionary<string, object> { ["type"] = "exported", ["text"] = text }));
    }
}
