using System;
using System.Drawing;
using System.Windows.Forms;
using System.IO;
using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Builder;

public class PainelControleForm : Form
{
    private readonly WebApplication _app;
    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _updateTimer;
    
    // Controles visuais
    private Label _lblStatusServidor = null!;
    private Label _lblCpu = null!;
    private Label _lblRam = null!;
    private Label _lblGpu = null!;
    private Label _lblVram = null!;
    private Label _lblNvenc = null!;
    private Label _lblFpsMosaico = null!;
    private Label _lblFpsVertical = null!;
    private Label _lblFontesAtivas = null!;
    private RichTextBox _rtbLogs = null!;
    
    // Métricas de CPU temporais
    private DateTime _ultimoTempoCpu = DateTime.UtcNow;
    private TimeSpan _ultimoTempoCpuProcesso = Process.GetCurrentProcess().TotalProcessorTime;

    public PainelControleForm(WebApplication app)
    {
        _app = app;
        
        // Configurações básicas da janela
        this.Text = "NDI Director - Painel de Controle";
        this.Size = new Size(1024, 640);
        this.MinimumSize = new Size(900, 550);
        this.StartPosition = FormStartPosition.CenterScreen;
        this.BackColor = Color.FromArgb(11, 14, 20); // Fundo Escuro Premium
        this.ForeColor = Color.FromArgb(220, 225, 235);
        
        // Define o ícone da janela de forma robusta
        Icon? appIcon = null;
        try
        {
            // Tenta obter o ícone embutido no executável principal
            string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                appIcon = Icon.ExtractAssociatedIcon(exePath);
            }
        }
        catch {}

        if (appIcon == null)
        {
            try
            {
                string[] caminhosPossiveis = new[]
                {
                    Path.Combine(AppContext.BaseDirectory, "icon.ico"),
                    Path.Combine(AppContext.BaseDirectory, "..\\..\\..\\icon.ico"),
                    Path.Combine(AppContext.BaseDirectory, "..\\assets\\icon.ico"),
                    "icon.ico"
                };
                
                string? iconePath = caminhosPossiveis.FirstOrDefault(File.Exists);
                if (!string.IsNullOrEmpty(iconePath))
                {
                    appIcon = new Icon(iconePath);
                }
            }
            catch {}
        }

        if (appIcon == null)
        {
            appIcon = SystemIcons.Application;
        }

        this.Icon = appIcon;

        // Inicializa Componentes da Interface
        InicializarLayout();

        // Configuração da System Tray (NotifyIcon)
        _notifyIcon = new NotifyIcon();
        _notifyIcon.Icon = appIcon;
        _notifyIcon.Text = "NDI Director - Servidor Local";
        _notifyIcon.Visible = true;
        _notifyIcon.DoubleClick += (s, e) => RestaurarJanela();

        // Menu de contexto da System Tray
        var menuBandeja = new ContextMenuStrip();
        menuBandeja.Items.Add("Abrir Painel de Controle", null, (s, e) => RestaurarJanela());
        menuBandeja.Items.Add("Abrir Painel Web (Navegador)", null, (s, e) => AbrirLinkWeb("http://localhost:8634"));
        menuBandeja.Items.Add("Abrir OBS Dock (Navegador)", null, (s, e) => AbrirLinkWeb("http://localhost:8634/dock"));
        menuBandeja.Items.Add("-");
        menuBandeja.Items.Add("Encerrar NDI Director", null, (s, e) => EncerrarAplicacao());
        _notifyIcon.ContextMenuStrip = menuBandeja;

        // Se inscreve para receber os logs em tempo real na caixa de texto
        LogManager.AoEscreverLog += OnLogRecebido;

        // Timer de atualização da telemetria a cada 1 segundo
        _updateTimer = new System.Windows.Forms.Timer();
        _updateTimer.Interval = 1000;
        _updateTimer.Tick += (s, e) => AtualizarTelemetria();
        _updateTimer.Start();
        
        // Força uma primeira atualização de telemetria
        AtualizarTelemetria();
    }

    private void InicializarLayout()
    {
        // -------------------------------------------------------------
        // PAINEL LATERAL (Status, Métricas e Botões)
        // -------------------------------------------------------------
        var painelLateral = new Panel
        {
            Dock = DockStyle.Left,
            Width = 280,
            BackColor = Color.FromArgb(17, 20, 28), // Fundo mais escuro para destaque lateral
            Padding = new Padding(15)
        };
        this.Controls.Add(painelLateral);

        // Logo/Título
        var lblLogo = new Label
        {
            Text = "NDI DIRECTOR",
            Font = new Font("Segoe UI", 16, FontStyle.Bold),
            ForeColor = Color.FromArgb(99, 102, 241), // Indigo Neon
            AutoSize = true,
            Location = new Point(15, 15)
        };
        painelLateral.Controls.Add(lblLogo);

        var lblSub = new Label
        {
            Text = "Servidor de Mosaico Local",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
            ForeColor = Color.FromArgb(120, 125, 135),
            AutoSize = true,
            Location = new Point(16, 42)
        };
        painelLateral.Controls.Add(lblSub);

        // Separador
        var sep = new Panel
        {
            Size = new Size(250, 1),
            BackColor = Color.FromArgb(30, 35, 48),
            Location = new Point(15, 65)
        };
        painelLateral.Controls.Add(sep);

        // Grupo: Status do Servidor
        _lblStatusServidor = new Label
        {
            Text = "Status: Conectando...",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(16, 185, 129), // Verde
            AutoSize = true,
            Location = new Point(15, 80)
        };
        painelLateral.Controls.Add(_lblStatusServidor);

        // Grupo: Telemetria de Hardware
        var lblHeaderTele = new Label
        {
            Text = "TELEMETRIA:",
            Font = new Font("Segoe UI", 8, FontStyle.Bold),
            ForeColor = Color.FromArgb(99, 102, 241),
            AutoSize = true,
            Location = new Point(15, 115)
        };
        painelLateral.Controls.Add(lblHeaderTele);

        _lblCpu = new Label { Text = "CPU: --%", Font = new Font("Segoe UI", 9), AutoSize = true, Location = new Point(15, 135) };
        _lblRam = new Label { Text = "RAM: -- MB", Font = new Font("Segoe UI", 9), AutoSize = true, Location = new Point(15, 155) };
        _lblGpu = new Label { Text = "GPU NVIDIA: --%", Font = new Font("Segoe UI", 9), AutoSize = true, Location = new Point(15, 175) };
        _lblVram = new Label { Text = "VRAM: -- / -- GB", Font = new Font("Segoe UI", 9), AutoSize = true, Location = new Point(15, 195) };
        _lblNvenc = new Label { Text = "Encoder NVENC: --", Font = new Font("Segoe UI", 9), AutoSize = true, Location = new Point(15, 215) };
        
        painelLateral.Controls.Add(_lblCpu);
        painelLateral.Controls.Add(_lblRam);
        painelLateral.Controls.Add(_lblGpu);
        painelLateral.Controls.Add(_lblVram);
        painelLateral.Controls.Add(_lblNvenc);

        // Grupo: Saídas NDI Mosaico
        var lblHeaderNdi = new Label
        {
            Text = "SAÍDAS NDI (MOSAICO):",
            Font = new Font("Segoe UI", 8, FontStyle.Bold),
            ForeColor = Color.FromArgb(99, 102, 241),
            AutoSize = true,
            Location = new Point(15, 250)
        };
        painelLateral.Controls.Add(lblHeaderNdi);

        _lblFpsMosaico = new Label { Text = "Horizontal: -- FPS", Font = new Font("Segoe UI", 9), AutoSize = true, Location = new Point(15, 270) };
        _lblFpsVertical = new Label { Text = "Vertical: -- FPS", Font = new Font("Segoe UI", 9), AutoSize = true, Location = new Point(15, 290) };
        _lblFontesAtivas = new Label { Text = "Feeds Ativos na Cena: 0/4", Font = new Font("Segoe UI", 9), AutoSize = true, Location = new Point(15, 310) };
        
        painelLateral.Controls.Add(_lblFpsMosaico);
        painelLateral.Controls.Add(_lblFpsVertical);
        painelLateral.Controls.Add(_lblFontesAtivas);

        // Grupo: Botões de Ação
        var btnAlternarMotor = CriarBotaoCustom("⚡ Alternar Motor (GPU/CPU)", Color.FromArgb(49, 46, 129), new Point(15, 350));
        btnAlternarMotor.Click += (s, e) =>
        {
            string proximo = AppConfig.MotorVideo == "gpu" ? "cpu" : "gpu";
            Task.Run(() => VideoEngineManager.ReiniciarMotor(proximo));
        };
        painelLateral.Controls.Add(btnAlternarMotor);

        var btnPainelWeb = CriarBotaoCustom("🖥️  Abrir Painel Web", Color.FromArgb(79, 70, 229), new Point(15, 395));
        btnPainelWeb.Click += (s, e) => AbrirLinkWeb("http://localhost:8634");
        painelLateral.Controls.Add(btnPainelWeb);

        var btnObsDock = CriarBotaoCustom("⚓  Abrir Dock do OBS", Color.FromArgb(30, 35, 48), new Point(15, 440));
        btnObsDock.Click += (s, e) => AbrirLinkWeb("http://localhost:8634/dock");
        painelLateral.Controls.Add(btnObsDock);

        var btnMinimizar = CriarBotaoCustom("📥  Minimizar para Tray", Color.FromArgb(41, 47, 66), new Point(15, 485));
        btnMinimizar.Click += (s, e) => { this.Hide(); };
        painelLateral.Controls.Add(btnMinimizar);

        var btnSair = CriarBotaoCustom("❌  Sair e Desligar", Color.FromArgb(153, 27, 27), new Point(15, 530));
        btnSair.Click += (s, e) => EncerrarAplicacao();
        painelLateral.Controls.Add(btnSair);

        // -------------------------------------------------------------
        // ÁREA CENTRAL/DIREITA (Console de Logs com Ancoragem)
        // -------------------------------------------------------------
        var lblLogsHeader = new Label
        {
            Text = "TERMINAL DE LOGS DO SISTEMA EM TEMPO REAL:",
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            ForeColor = Color.FromArgb(160, 165, 175),
            Location = new Point(295, 18),
            Size = new Size(500, 20),
            Anchor = AnchorStyles.Top | AnchorStyles.Left
        };
        this.Controls.Add(lblLogsHeader);

        _rtbLogs = new RichTextBox
        {
            Location = new Point(295, 45),
            Size = new Size(this.ClientSize.Width - 310, this.ClientSize.Height - 65),
            BackColor = Color.FromArgb(17, 20, 28),
            ForeColor = Color.FromArgb(163, 172, 191),
            Font = new Font("Consolas", 9.5f, FontStyle.Regular),
            BorderStyle = BorderStyle.None,
            ReadOnly = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Text = "[*] Inicializando Painel de Controle Desktop...\n"
        };
        this.Controls.Add(_rtbLogs);
    }

    private Button CriarBotaoCustom(string texto, Color corFundo, Point localizacao)
    {
        return new Button
        {
            Text = texto,
            Size = new Size(250, 42),
            Location = localizacao,
            FlatStyle = FlatStyle.Flat,
            BackColor = corFundo,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Cursor = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleCenter
        };
    }

    private void OnLogRecebido(string msg)
    {
        if (this.IsDisposed) return;
        
        try
        {
            this.BeginInvoke(new Action(() =>
            {
                if (this.IsDisposed) return;
                
                _rtbLogs.AppendText(msg + "\n");
                
                // Trunca texto se ficar excessivamente longo na tela
                if (_rtbLogs.TextLength > 30000)
                {
                    _rtbLogs.Text = _rtbLogs.Text.Substring(15000);
                }
                
                _rtbLogs.SelectionStart = _rtbLogs.TextLength;
                _rtbLogs.ScrollToCaret();
            }));
        }
        catch {}
    }

    private void AtualizarTelemetria()
    {
        // 1. Status do Servidor
        _lblStatusServidor.Text = $"Status: ONLINE (Porta 8634)";
        
        // 2. CPU e RAM do Processo
        try
        {
            double cpuPorcentagem = 0;
            var tempoAtual = DateTime.UtcNow;
            var tempoCpuProcesso = Process.GetCurrentProcess().TotalProcessorTime;
            var tempoDecorrido = tempoAtual - _ultimoTempoCpu;
            if (tempoDecorrido.TotalMilliseconds > 100)
            {
                var cpuDiferenca = tempoCpuProcesso - _ultimoTempoCpuProcesso;
                cpuPorcentagem = (cpuDiferenca.TotalMilliseconds / (tempoDecorrido.TotalMilliseconds * Environment.ProcessorCount)) * 100;
                cpuPorcentagem = Math.Round(Math.Max(0.0, Math.Min(100.0, cpuPorcentagem)), 1);
            }
            _ultimoTempoCpu = tempoAtual;
            _ultimoTempoCpuProcesso = tempoCpuProcesso;
            _lblCpu.Text = $"CPU do Processo: {cpuPorcentagem}%";

            long bytesRam = Process.GetCurrentProcess().WorkingSet64;
            double ramMb = Math.Round(bytesRam / 1024.0 / 1024.0, 1);
            _lblRam.Text = $"Memória RAM: {ramMb} MB";
        }
        catch
        {
            _lblCpu.Text = "CPU do Processo: Erro ao obter";
            _lblRam.Text = "Memória RAM: Erro ao obter";
        }

        // 3. GPU NVIDIA (NVML)
        try
        {
            var (nvencLoad, nvencSessions, gpuLoad, vramUsed, vramTotal) = NvidiaGpuMonitor.ObterMetricas();
            if (vramTotal.HasValue && vramUsed.HasValue && vramTotal.Value > 0)
            {
                double vramUsadaGb = Math.Round((double)vramUsed.Value / 1024.0, 2);
                double vramTotalGb = Math.Round((double)vramTotal.Value / 1024.0, 2);
                _lblGpu.Text = $"GPU NVIDIA: {gpuLoad}% Uso";
                _lblVram.Text = $"VRAM: {vramUsadaGb} GB / {vramTotalGb} GB";
                _lblNvenc.Text = $"Encoder NVENC: {nvencLoad}% ({nvencSessions} ativas)";
            }
            else
            {
                _lblGpu.Text = "GPU NVIDIA: Inativa ou sem NVML";
                _lblVram.Text = "VRAM: --";
                _lblNvenc.Text = "Encoder NVENC: --";
            }
        }
        catch
        {
            _lblGpu.Text = "GPU NVIDIA: Erro ao monitorar";
        }

        // 4. Outputs NDI Mosaico
        try
        {
            double fpsMosaico = AppConfig.MotorVideo == "gpu" ? VideoEngineGpu.ObterFpsMosaico() : VideoEngine.ObterFpsMosaico();
            double fpsVertical = AppConfig.MotorVideo == "gpu" ? VideoEngineGpu.ObterFpsVertical() : VideoEngine.ObterFpsVertical();
            _lblFpsMosaico.Text = $"Mosaico Horiz: {fpsMosaico} FPS ({(AppConfig.MotorVideo == "gpu" ? "GPU" : "CPU")})";
            _lblFpsVertical.Text = $"Mosaico Vert: {fpsVertical} FPS";
            
            int countCena = 0;
            lock (AppConfig.LockFontes)
            {
                for (int i = 0; i < 4; i++)
                {
                    if (!string.IsNullOrEmpty(AppConfig.OrdemReceptores[i]))
                    {
                        countCena++;
                    }
                }
            }
            _lblFontesAtivas.Text = $"Feeds Ativos na Cena: {countCena}/4";
        }
        catch
        {
            _lblFpsMosaico.Text = "Horizontal: -- FPS";
            _lblFpsVertical.Text = "Vertical: -- FPS";
            _lblFontesAtivas.Text = "Feeds Ativos na Cena: --";
        }
    }

    private void RestaurarJanela()
    {
        this.Show();
        this.WindowState = FormWindowState.Normal;
        this.Activate();
    }

    private void AbrirLinkWeb(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            LogManager.Escrever($"[!] Erro ao abrir link no navegador: {ex.Message}");
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Se o fechamento foi disparado pelo clique do usuário no botão [X] da janela
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            this.Hide();
            _notifyIcon.ShowBalloonTip(1500, "NDI Director", "O servidor continua rodando em segundo plano. Use o ícone perto do relógio para abrir ou fechar de vez.", ToolTipIcon.Info);
        }
        else
        {
            base.OnFormClosing(e);
        }
    }

    private async void EncerrarAplicacao()
    {
        var result = MessageBox.Show(
            "Tem certeza que deseja encerrar o NDI Director?\n\nIsso desligará o servidor web, as saídas NDI do Mosaico e cancelará todas as gravações ativas de feeds.",
            "Confirmar Encerramento",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2
        );

        if (result == DialogResult.Yes)
        {
            // Oculta a janela e a bandeja de imediato para resposta instantânea ao usuário
            this.Hide();
            _notifyIcon.Visible = false;
            Application.DoEvents(); // Garante que o Windows processe a ocultação da janela na hora

            _updateTimer.Stop();
            LogManager.AoEscreverLog -= OnLogRecebido;
            
            try
            {
                // Inicia a parada do servidor web e NDI, mas limita a espera a no máximo 1.5 segundos
                // para não travar o processo devido a conexões SSE (Server-Sent Events) pendentes no navegador
                var stopTask = _app.StopAsync();
                await Task.WhenAny(stopTask, Task.Delay(1500));
            }
            catch {}

            Application.Exit();
            Environment.Exit(0);
        }
    }
}
