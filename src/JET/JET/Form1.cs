using JET.Application;
using JET.Bridge;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace JET
{
    public partial class Form1 : Form, IHostShell
    {
        private const string AppHostName = "app.jet.local";

        private readonly ActionDispatcher _dispatcher;
        private readonly WebViewEventPublisher _eventPublisher = new();
        private WebView2? _webView;
        private JetWebMessageBridge? _bridge;

        public Form1()
        {
            // 不能用 this(...) ctor chaining：constructor initializer 內不可引用 this
            _dispatcher = AppCompositionRoot.CreateDispatcher(this, _eventPublisher);

            InitializeComponent();
            ConfigureWindowChrome();
            InitializeWebViewHost();
        }

        public Form1(ActionDispatcher dispatcher)
        {
            _dispatcher = dispatcher;

            InitializeComponent();
            ConfigureWindowChrome();
            InitializeWebViewHost();
        }

        /// <summary>
        /// 視窗外觀屬於 host chrome（guide §12），designer 檔不可手改，
        /// 故在此覆寫預設的 800×450：前端三欄佈局至少需要約 1000px 寬。
        /// </summary>
        private void ConfigureWindowChrome()
        {
            Text = "JET 傳票測試工具";

            var workArea = Screen.PrimaryScreen?.WorkingArea
                ?? new Rectangle(0, 0, 1280, 800);

            ClientSize = new Size(
                Math.Min(1280, workArea.Width - 80),
                Math.Min(800, workArea.Height - 80));
            MinimumSize = new Size(1024, 640);
            StartPosition = FormStartPosition.CenterScreen;
        }

        /// <summary>
        /// host.selectFile 的原生能力：開啟 OpenFileDialog。
        /// 純 host capability，不含業務邏輯（guide §12 Host）。
        /// </summary>
        public Task<string?> PickOpenFileAsync(
            string title,
            IReadOnlyList<string> extensions,
            CancellationToken cancellationToken)
        {
            return ShowDialogDeferredAsync<string?>(() =>
            {
                using var dialog = new OpenFileDialog
                {
                    Title = title,
                    Filter = BuildFilter(extensions),
                    CheckFileExists = true
                };

                return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
            });
        }

        /// <summary>
        /// host.selectFiles 的原生能力：多選檔案對話框（匯入精靈用）。取消 = 空清單。
        /// </summary>
        public Task<IReadOnlyList<string>> PickOpenFilesAsync(
            string title,
            IReadOnlyList<string> extensions,
            CancellationToken cancellationToken)
        {
            return ShowDialogDeferredAsync<IReadOnlyList<string>>(() =>
            {
                using var dialog = new OpenFileDialog
                {
                    Title = title,
                    Filter = BuildFilter(extensions),
                    CheckFileExists = true,
                    Multiselect = true
                };

                return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileNames : [];
            });
        }

        /// <summary>
        /// host.selectSavePath 的原生能力：開啟 SaveFileDialog 取得匯出底稿存檔路徑。
        /// 預填檔名 {base}_{yyyymmddHHmmss}_WorkingPaper.xlsx——**時間戳在此 host 端以 DateTime 產生**
        /// （非 Domain：把「現在時間」這個環境輸入留在 host，業務層只收最終路徑）。取消回 null。
        /// 純 host capability，不含業務邏輯（guide §12 Host；鏡射 PickOpenFileAsync）。
        /// </summary>
        public Task<string?> PickSavePathAsync(string baseFileName, CancellationToken cancellationToken)
        {
            var suggestedName = $"{baseFileName}_{DateTime.Now:yyyyMMddHHmmss}_WorkingPaper.xlsx";

            return ShowDialogDeferredAsync<string?>(() =>
            {
                using var dialog = new SaveFileDialog
                {
                    Title = "匯出底稿",
                    Filter = "Excel 活頁簿 (*.xlsx)|*.xlsx",
                    DefaultExt = "xlsx",
                    AddExtension = true,
                    OverwritePrompt = true,
                    FileName = suggestedName
                };

                return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
            });
        }

        /// <summary>
        /// host.exitApp 的原生能力：關閉應用程式視窗。
        /// BeginInvoke 讓關閉動作排在目前 WebMessage 處理之後，避免在 bridge 回應途中拆掉 WebView。
        /// </summary>
        public void RequestExit()
        {
            BeginInvoke(Close);
        }

        /// <summary>
        /// host.openFolder 的原生能力：在檔案總管揭示路徑（開啟所在目錄並選取該檔）。
        /// 用 explorer /select 一步到位（開資料夾並反白檔案）；純 host I/O,不阻斷 action 回應。
        /// </summary>
        public Task RevealInExplorerAsync(string path, CancellationToken cancellationToken)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
            return Task.CompletedTask;
        }

        private static string BuildFilter(IReadOnlyList<string> extensions)
        {
            if (extensions.Count == 0)
            {
                return "所有檔案 (*.*)|*.*";
            }

            var patterns = string.Join(";", extensions.Select(e => $"*{e}"));
            return $"支援的檔案 ({patterns})|{patterns}|所有檔案 (*.*)|*.*";
        }

        /// <summary>
        /// 在 WebView2 事件處理常式返回後、於 UI 訊息泵的下一個回合才彈出 modal 對話框,避免 reentrancy 崩潰。
        ///
        /// WebView2 不支援在它的事件處理常式(含 <c>WebMessageReceived</c>,bridge 由此進入)內**同步**開啟
        /// modal UI / 巢狀訊息迴圈(如 <c>OpenFileDialog.ShowDialog</c>)。這麼做會觸發 reentrancy,瀏覽器程序
        /// 以 <c>0x80000003</c> 崩潰、整個 app 閃退(無 .NET 例外可見)。權威:WebView2「Threading model」的
        /// Reentrancy 章節明載「請把工作排到事件處理常式完成之後再執行」;社群同簽章重現見 WebView2Feedback
        /// #2946 / #4648 / #3028。
        ///
        /// 作法:用 <see cref="System.Windows.Forms.Control.BeginInvoke(Delegate)"/> 把對話框排進 UI 訊息泵的
        /// 下一回合。bridge 端 <c>await</c> 這個尚未完成的 Task 時,<c>WebMessageReceived</c> 會先返回、原生回呼
        /// 退棧,排入的對話框才在頂層(非重入)執行,結果再經 Task 回傳。語意等同官方範例的
        /// <c>SynchronizationContext.Current.Post</c>,但 <c>BeginInvoke</c> 不依賴 <c>SynchronizationContext.Current</c>
        /// 非空、可從任何執行緒穩健 marshal 回 UI 緒,並與本檔 <see cref="RequestExit"/> 既有的延遲手法一致。
        /// </summary>
        private Task<T> ShowDialogDeferredAsync<T>(Func<T> showDialog)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            BeginInvoke(new Action(() =>
            {
                try
                {
                    completion.SetResult(showDialog());
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            }));

            return completion.Task;
        }

        private void InitializeWebViewHost()
        {
            _webView = new WebView2
            {
                Dock = DockStyle.Fill
            };

            Controls.Add(_webView);
            _webView.BringToFront();

            Load += OnFormLoad;
        }

        private async void OnFormLoad(object? sender, EventArgs e)
        {
            try
            {
                await InitializeWebViewAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    ex.Message,
                    "JET WebView2 initialization failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private async Task InitializeWebViewAsync()
        {
            if (_webView is null)
            {
                throw new InvalidOperationException("WebView2 host was not initialized.");
            }

            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JET",
                "WebView2");

            Directory.CreateDirectory(userDataFolder);

            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder,
                options: null);

            await _webView.EnsureCoreWebView2Async(environment);

            var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            if (!Directory.Exists(wwwroot))
            {
                throw new DirectoryNotFoundException($"JET wwwroot was not found: {wwwroot}");
            }

            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                AppHostName,
                wwwroot,
                CoreWebView2HostResourceAccessKind.DenyCors);

            _bridge = new JetWebMessageBridge(_webView.CoreWebView2, _dispatcher);
            _bridge.Attach();

            // 事件推播綁定（host→web）：在 UI 執行緒捕捉 SynchronizationContext，
            // 供背景緒的 Publish marshal 回 UI 執行緒呼叫 PostWebMessageAsJson
            if (SynchronizationContext.Current is { } uiContext)
            {
                _eventPublisher.Bind(_webView.CoreWebView2, uiContext);
            }

            _webView.CoreWebView2.Navigate($"https://{AppHostName}/index.html");
        }
    }
}
