using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BoramRms.Setup
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                if (args.Length == 2 && args[0] == "--self-test") return SetupTests.Run(Path.GetFullPath(args[1]));
                if (args.Length == 2 && args[0] == "--font-probe") return SetupTests.FontProbe(Path.GetFullPath(args[1]));
                if (args.Length != 0) throw new ArgumentException("설치 프로그램은 더블클릭하여 사용하세요.");
                if (!Environment.Is64BitOperatingSystem || Environment.OSVersion.Version.Major < 10) throw new NotSupportedException("Windows 10/11 64비트용 설치 파일입니다.");
                Application.Run(new SetupForm()); return 0;
            }
            catch (Exception e) { MessageBox.Show(e.Message, "보람 RMS Lite 설치", MessageBoxButtons.OK, MessageBoxIcon.Warning); return 1; }
        }
    }
    public sealed class SetupForm : Form
    {
        private readonly CheckBox desktop = new CheckBox { Text = "바탕화면 바로가기 만들기", Checked = true, AutoSize = true };
        private readonly CheckBox startMenu = new CheckBox { Text = "시작 메뉴에 추가", Checked = true, AutoSize = true };
        private readonly CheckBox font = new CheckBox { Text = "Pretendard 공식 다운로드·설치 (선택, 인터넷 필요)", Checked = false, AutoSize = true };
        private readonly Button install = new Button { Text = "설치 시작", AutoSize = true, Padding = new Padding(14, 5, 14, 5) };
        private readonly Button launch = new Button { Text = "앱 실행", AutoSize = true, Visible = false, Padding = new Padding(14, 5, 14, 5) };
        private readonly Label state = new Label { Text = "앱 설치는 인터넷·GitHub 로그인 없이 진행됩니다.", AutoSize = true, MaximumSize = new Size(580, 0) };
        private readonly ProgressBar bar = new ProgressBar { Dock = DockStyle.Top, Height = 8, Style = ProgressBarStyle.Blocks };
        private bool busy;
        public SetupForm()
        {
            Text = "보람 RMS Lite · 처음 설치"; Font = new Font("Malgun Gothic", 10); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(650, 510); MinimumSize = new Size(620, 520); StartPosition = FormStartPosition.CenterScreen; BackColor = Color.White;
            using (var stream = InstallCore.Resource("brand.ico")) Icon = new Icon(stream);
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(22, 16, 22, 16) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); Controls.Add(root);
            var header = new Label { Text = "보람 RMS Lite " + InstallCore.Info().Version + " 설치", AutoSize = true, Font = new Font(Font.FontFamily, 21, FontStyle.Bold), ForeColor = Color.FromArgb(38, 123, 75), Margin = new Padding(0,0,0,12) }; root.Controls.Add(header, 0, 0);
            var body = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            root.Controls.Add(body, 0, 1);
            AddText(body, "1  설치 파일 하나로 앱과 실행 환경까지 준비합니다.");
            AddText(body, "Python·Codex·별도 .NET 설치는 필요하지 않습니다.");
            AddText(body, "2  현재 Windows 사용자에게만 설치합니다.\n관리자 권한으로 실행하지 마세요. 기존 RMS·신청서는 건드리지 않습니다.");
            AddText(body, "설치 위치", true); var location = new TextBox { Text = InstallCore.AppDirectory, ReadOnly = true, Width = 575, BorderStyle = BorderStyle.FixedSingle }; body.Controls.Add(location);
            desktop.Margin = new Padding(0,12,0,3); body.Controls.Add(desktop); body.Controls.Add(startMenu);
            font.Margin = new Padding(0,8,0,3); body.Controls.Add(font);
            AddText(body, "미선택해도 맑은 고딕으로 사용할 수 있습니다. 이미 있는 서체는 교체하지 않습니다.");
            var license = new LinkLabel { Text = "Pretendard 공식 배포처 · 라이선스 보기", AutoSize = true };
            license.LinkClicked += (_, __) => Open("https://github.com/orioncactus/pretendard#라이선스"); body.Controls.Add(license);
            AddText(body, "3  설치 후 ‘앱 실행’ → ‘+ 폴더 선택’으로 시작하세요.\n다음 업데이트는 앱의 ‘업데이트 확인’에서 진행합니다.");
            var footer = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(0,12,0,0) };
            footer.Controls.Add(bar); state.Margin = new Padding(0,8,0,8); footer.Controls.Add(state);
            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            install.BackColor = Color.FromArgb(38,123,75); install.ForeColor = Color.White; install.FlatStyle = FlatStyle.Flat;
            var close = new Button { Text = "닫기", AutoSize = true, Padding = new Padding(12,5,12,5) }; close.Click += (_, __) => Close();
            actions.Controls.Add(install); actions.Controls.Add(launch); actions.Controls.Add(close); footer.Controls.Add(actions); root.Controls.Add(footer,0,2);
            install.Click += async (_, __) => await InstallAsync(); launch.Click += (_, __) => Open(Path.Combine(InstallCore.AppDirectory, InstallCore.Executable));
            FormClosing += (_, e) => { if (busy) { e.Cancel = true; state.Text = "설치가 진행 중입니다. 완료 후 닫아 주세요."; } };
        }
        private void AddText(Control parent, string text, bool bold = false)
        {
            parent.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(575, 0), Margin = new Padding(0,5,0,5), Font = bold ? new Font(Font, FontStyle.Bold) : Font });
        }
        private static void Open(string path) { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception e) { MessageBox.Show(e.Message, "보람 RMS Lite"); } }
        private async Task InstallAsync()
        {
            if (busy) return; busy = true; install.Enabled = false; desktop.Enabled = startMenu.Enabled = font.Enabled = false;
            bar.Style = ProgressBarStyle.Marquee; var warnings = new List<string>();
            var progress = new Progress<string>(s => state.Text = s);
            try
            {
                var result = await Task.Run(() => InstallCore.Install(InstallCore.AppDirectory, progress));
                var exe = Path.Combine(result.Directory, InstallCore.Executable);
                try { InstallCore.WriteGuide(InstallCore.BaseDirectory); } catch (Exception e) { warnings.Add("설치 안내 저장: " + e.Message); }
                if (desktop.Checked) try { InstallCore.Shortcut(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), exe); } catch (Exception e) { warnings.Add("바탕화면 바로가기: " + e.Message); }
                if (startMenu.Checked) try { InstallCore.Shortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Boram RMS Lite"), exe); } catch (Exception e) { warnings.Add("시작 메뉴: " + e.Message); }
                string fontResult = "추가 서체 없이 기본 글꼴을 사용합니다.";
                if (font.Checked) try { fontResult = await Task.Run(() => FontInstaller.InstallForCurrentUser(progress)); } catch (Exception e) { warnings.Add("선택 서체는 설치를 완료하지 못했습니다: " + e.Message + " 앱은 맑은 고딕으로 사용 가능합니다."); }
                state.Text = (result.AlreadyInstalled ? "기존 앱 유지 · 덮어쓰기하지 않았습니다. 업데이트는 앱에서 진행하세요." : "설치 완료 · 검증된 앱과 실행 환경이 준비됐습니다.") + "\n" + fontResult;
                if (warnings.Count > 0) state.Text += "\n일부 선택 작업 확인 필요: " + string.Join(" / ", warnings);
                launch.Visible = true; install.Visible = false; bar.Style = ProgressBarStyle.Blocks; bar.Value = 100;
            }
            catch (Exception e) { state.Text = "설치를 완료하지 못했습니다: " + e.Message; install.Enabled = true; desktop.Enabled = startMenu.Enabled = font.Enabled = true; bar.Style = ProgressBarStyle.Blocks; }
            finally { busy = false; }
        }
    }
}
