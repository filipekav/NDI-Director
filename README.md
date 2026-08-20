# NDI Director

Sistema de controle e composição de vídeo NDI em tempo real, com painel web para gerenciamento de fontes, gravação individual por GPU (NVENC), e saída de mosaico NDI.

O projeto adota uma **arquitetura modularizada por domínio**, separando as responsabilidades de rede (NDI), motores de vídeo e áudio, gravação via FFmpeg, e serviços web/SSE.

---

## Requisitos

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (Windows)
- [NDI Runtime](https://ndi.video/tools/) instalado na máquina
- [FFmpeg](https://ffmpeg.org/download.html) adicionado ao PATH do Windows (obrigatório para gravação)
- GPU NVIDIA com drivers atualizados (para codificação NVENC acelerada por hardware e telemetria de GPU)

---

## Estrutura do Projeto

```
NDI Director/
├── src/                        # Código-fonte C# (.NET 8)
│   ├── Audio/                  # Gerenciamento e mixagem de áudio (AudioMixer.cs)
│   ├── Config/                 # Gerenciamento de configurações e JSON (AppConfig.cs, ConfigData.cs)
│   ├── Gpu/                    # Telemetria e monitoramento de GPU NVIDIA (NvidiaGpuMonitor.cs)
│   ├── Gravacao/               # Gravação individual via FFmpeg (GravadorFFmpeg.cs, MuxingStatus.cs)
│   ├── Helpers/                # Utilitários gerais (CaminhoHelper.cs, TimePrefixedTextWriter.cs, LogManager.cs, PainelControleForm.cs)
│   ├── Ndi/                    # Descoberta e recepção de feeds NDI (NdiScanner.cs, ReceptorNDI.cs)
│   ├── Video/                  # Motor de vídeo e composição do mosaico NDI (VideoEngine.cs, PosicaoFeed.cs)
│   ├── Web/                    # Servidor Web, SSE e rotas da API (SseManager.cs)
│   │   └── Routes/             # Rotas mapeadas por domínio (ConfigRoutes.cs, FontesRoutes.cs, GravacaoRoutes.cs, SseRoutes.cs)
│   ├── Program.cs              # Bootstrap principal (inicializa interface gráfica WinForms e servidores)
│   └── NdiDirector.csproj
├── web/                        # Interface web (HTML/CSS/JS)
│   ├── static/                 # Recursos estáticos
│   │   ├── css/                # Folhas de estilo (comum.css)
│   │   └── js/                 # Lógica do painel web (comum.js)
│   └── templates/
│       ├── painel.html         # Painel principal de controle
│       └── dock.html           # Dock compacto e responsivo para OBS
├── assets/                     # Recursos estáticos globais
│   └── ANTON-REGULAR.TTF       # Fonte para os lower-thirds (GC)
├── tools/                      # Ferramentas e utilitários auxiliares
│   ├── gerador_ndi.py          # Gerador de feeds NDI a partir de arquivos de vídeo locais
│   ├── gerar_video_sincronia.py # Gera um vídeo padrão de sincronia para testes
│   ├── analisar_audio_ndi.py   # Script para análise do comportamento do áudio NDI
│   └── requirements.txt
├── dist/                       # Build de distribuição compilado (gerado)
├── .github/workflows/build.yml # Esteira de CI/CD (GitHub Actions)
├── .gitignore
└── README.md
```

---

## Como Executar (Desenvolvimento)

```bash
cd src
dotnet run
```

Ao executar o comando no Windows, o sistema inicializará a **janela gráfica de controle nativa (WinForms)** contendo uma área dedicada para exibição de logs em tempo real (TUI). O servidor web será iniciado na porta **8634** em background. Acesse:
- **Painel principal:** http://localhost:8634/
- **Dock OBS:** http://localhost:8634/dock

---

## Como Gerar o Build de Distribuição

```bash
cd src
dotnet publish -c Release -o ../dist
```

A pasta `dist/` conterá todos os arquivos necessários para executar em qualquer máquina Windows com .NET 8 Runtime instalado (incluindo o executável principal, os templates HTML e a fonte Anton). Basta copiar a pasta inteira e executar `NdiDirector.exe`.

---

## Integração com OBS Studio (Custom Docks)

O NDI Director inclui uma interface otimizada para monitoramento e controle rápido de dentro do OBS Studio:

1. No OBS Studio, vá em **Painéis (Docks)** -> **Painéis Web Personalizados... (Custom Browser Docks...)**.
2. Adicione um novo painel com o nome `NDI Director` e a URL:
   `http://localhost:8634/dock`
3. Clique em **Aplicar** e posicione o dock onde preferir no seu layout do OBS.
4. O Dock permite selecionar as câmeras que vão para a cena principal, controlar volumes, aplicar Solo/Highlight, ver os níveis de VU e checar a telemetria da GPU de forma integrada.

---

## Gravação Individual por Fonte (ISO Recording)

A gravação individual (CFR) de cada feed NDI opera em segundo plano usando pipelines otimizados do FFmpeg:

* **Fallback Automático de Hardware:** O sistema tenta codificar o vídeo via **GPU NVIDIA (H.264 NVENC)** para poupar processador. Se a GPU atingir o limite físico de sessões de codificação concorrentes ou apresentar falha ao iniciar, o sistema realiza um **fallback silencioso para CPU (libx264 ultrafast)** em tempo de execução, garantindo que a gravação não falhe.
* **Sincronia Perfeita de Áudio (CFR):** O sistema realiza alinhamento de áudio adicionando silêncio (PCM) automaticamente caso ocorram perdas de pacotes ou frames na rede, mitigando desvios acumulados ao longo do tempo.
* **Destino das Gravações:** Os arquivos de saída `.mp4` finais são salvos diretamente na pasta de **Downloads** do usuário conectado (`C:\Users\<NomeDoUsuario>\Downloads\`), com o padrão de nomenclatura:
  `Gravacao_NDI_<NomeDaFonteSafe>_<Data>_<Hora>.mp4`
* **Muxing com Progresso:** Ao interromper uma gravação, o FFmpeg combina os arquivos de áudio e vídeo temporários gerados em um container `.mp4` final. O painel web exibe em tempo real o progresso de finalização (muxing) antes de liberar a gravação.

---

## Sincronização de Áudio e Vídeo (Auto Lip-Sync)

O NDI Director implementa uma arquitetura completa de sincronização labial (Lip-Sync) em tempo real, especialmente desenhada para solucionar assimetrias de latência e rajadas de pacotes de participantes originados do **Microsoft Teams**:

1. **Normalizador Elástico por Participante:** Cada feed do Teams é mantido suavemente em uma margem de segurança fixa de **40ms** (1.920 amostras a 48 kHz). Desvios de relógio (*clock drift*) e oscilações de rede são absorvidos via micro-resampling linear contínuo, sem descartes abruptos de áudio nem pausas bruscas.
2. **Auto-Calibração de Latência de Vídeo:** O sistema mede dinamicamente o tempo exato decorrido desde a chegada dos frames de vídeo até a renderização e transmissão pelo motor de vídeo (GPU DirectX 11 ou CPU OpenCV). O mixer aplica automaticamente o atraso correspondente na saída de áudio para sincronia labial perfeita.
3. **Thread Dedicada de Transmissão NDI (`MESA_NDI_AUDIO`):** O stream de áudio mixado roda em uma thread isolada de alta prioridade com temporizador de alta precisão (`Stopwatch`), transmitindo blocos a cada **20.0ms cravados** com carimbos monotônicos em ticks (`DateTime.UtcNow.Ticks`) alinhados com os quadros de vídeo.
4. **Telemetria e Ajuste Fino na Interface:** O painel web e o OBS Dock exibem em tempo real o status de `LIP-SYNC: ~20ms (AUTO)` e a profundidade de buffer de cada participante (`🔊 40ms`). É possível alternar para modo manual e aplicar offset fino de $\pm 300\text{ms}$ pelo modal de configurações.

> [!TIP]
> **Como configurar no OBS Studio:**
> - Nas propriedades das fontes NDI (`MESA_NDI_MOSAICO`, `MESA_NDI_VERTICAL` e `MESA_NDI_AUDIO`), configure a **Sincronização Áudio/Vídeo** como **`Timestamp da fonte NDI`** (*Source Timecode*).
> - Nas *Propriedades de Áudio Avançadas* do OBS, mantenha o **Atraso de Sincronização** em **`0 ms`** (o NDI Director realiza todo o alinhamento de forma automática).

---

## Configurações Persistentes (`ndi_director_config.json`)

As configurações da aplicação são gerenciadas e armazenadas no arquivo `ndi_director_config.json` localizado no diretório de execução. O arquivo é atualizado automaticamente a cada mudança na interface:

```json
{
  "MotorVideo": "gpu",
  "AutoLipSync": true,
  "AtrasoAudioManualMs": 0,
  "CorFundoAtual": "verde",
  "FormatoAudioAtual": "aac",
  "ApagarTemporarios": true,
  "QualidadeGravacao": "media",
  "HabilitarLivePreview": true,
  "HabilitarLogsDiagnostico": false,
  "MosaicoVertical": false,
  "PaddingMosaico": 20,
  "CanvasLarguraHorizontal": 1920,
  "CanvasAlturaHorizontal": 850,
  "CanvasLarguraVertical": 550,
  "CanvasAlturaVertical": 850,
  "LimiteSessoesNvenc": 8,
  "ApelidosFontes": {},
  "VolumesFontes": {}
}
```

### Principais Parâmetros:
* `MotorVideo`: Motor de composição de vídeo (`"gpu"` para Direct3D 11 / Direct2D acelerado, ou `"cpu"` para OpenCV / GDI+).
* `AutoLipSync`: Ativa/desativa a auto-calibração de atraso de áudio em tempo real (`true` por padrão).
* `AtrasoAudioManualMs`: Offset manual de atraso em milissegundos ($\pm 300\text{ms}$).
* `CorFundoAtual`: Cor de fundo do canvas do mosaico (opções: `"cinza"`, `"verde"`, `"azul"`, `"preto"`, `"transparente"`), ideal para Chroma Key.
* `QualidadeGravacao`: Define o fator de qualidade constante (CRF/QP) para as gravações (opções: `"alta"`, `"media"`, `"baixa"`).
* `FormatoAudioAtual`: Formato temporário de áudio utilizado na captura (opções: `"aac"` ou `"pcm"`).
* `LimiteSessoesNvenc`: Limite máximo de gravações paralelas permitidas por GPU antes de acionar o fallback de CPU.
* `MosaicoVertical`: Alterna a composição do mosaico para layout vertical (específico para plataformas de celular/stories).
* `ApelidosFontes` & `VolumesFontes`: Salva apelidos customizados definidos para os lower-thirds e ajustes de volume de cada câmera.

---

## Gerador de Feeds NDI (Testes)

Para simular fontes NDI usando arquivos de vídeo para testar o sistema localmente:

1. Acesse o diretório de ferramentas e instale as dependências necessárias:
   ```bash
   cd tools
   pip install -r requirements.txt
   ```
2. Crie o vídeo padrão de sincronia (opcional):
   ```bash
   python gerar_video_sincronia.py
   ```
3. Inicie a simulação de uma fonte NDI:
   ```bash
   python gerador_ndi.py
   ```

### Comportamento e Uso do `gerador_ndi.py`:

- **Sem argumentos:** Se executado sem argumentos (`python gerador_ndi.py`), o script busca por vídeos suportados (`.mp4`, `.webm`, `.mkv`, etc.) dentro da pasta `tools/` e cria um feed NDI utilizando o **primeiro vídeo encontrado**.
- **Com argumentos:** Você pode especificar um arquivo de vídeo específico passando o seu caminho como argumento:
  ```bash
  python gerador_ndi.py video_teste_sincronia.mp4
  ```
- **Simulação de Múltiplos Feeds:** O script processa e transmite um vídeo por vez. Para simular múltiplos feeds NDI concorrentes na rede local, você deve abrir novas abas/janelas do terminal e executar instâncias adicionais do script apontando para vídeos diferentes:
  ```bash
  # No Terminal 1
  python gerador_ndi.py video1.mp4

  # No Terminal 2
  python gerador_ndi.py video2.mp4
  ```

> **Nota:** Os vídeos de teste são ignorados pelo `.gitignore` por serem arquivos grandes.

---

## CI/CD (GitHub Actions)

O projeto inclui uma esteira automática do GitHub Actions em `.github/workflows/build.yml`.

Sempre que você fizer um push para a branch `main` ou criar uma Pull Request, o GitHub irá:
1. Compilar o projeto.
2. Gerar a pasta de publicação `dist/` contendo o executável, templates HTML e fontes.
3. Disponibilizar a pasta `dist/` como um **artefato zipado** na aba **Actions** e também fazer o upload automático para release.

---

## Funcionalidades

- **Interface Desktop WinForms:** Janela gráfica de controle principal nativa no Windows, com suporte a minimização para a bandeja do sistema (System Tray), ícone personalizado de status e caixa de confirmação nativa ao fechar para evitar interrupções acidentais.
- **TUI de Logs Integrada:** Monitoramento detalhado de eventos de conexão, conexões NDI, logs de gravação e telemetria integrado diretamente na tela da interface gráfica.
- **Arquitetura Modular:** Separação clara de responsabilidades por domínios (Áudio, Vídeo, Configuração, GPU, Gravação, NDI, Web/SSE).
- **Descoberta automática** de fontes NDI na rede local.
- **Mosaico de até 4 câmeras** com layout automático (horizontal/vertical) e renderização via NDI.
- **Highlight e Solo** para destaque de participantes na tela.
- **Gravação individual** por participante com FFmpeg (com suporte a NVENC via GPU ou CPU com detecção automática e fallback dinâmico).
- **Telemetria de GPU NVIDIA:** Exibição da carga do codificador (NVENC) e consumo de VRAM em tempo real na interface, com inicialização assíncrona inteligente.
- **Mixer de áudio** em tempo real com controle de volume independente por fonte e medidores (VU) integrados.
- **Lower-thirds (GC)** com fonte customizada, apelidos editáveis e suporte a estilos dinâmicos.
- **Tela de Standby NDI Premium:** Visual de gerador de caracteres e sinal profissional, contendo Safe Areas (90% Action e 80% Title) em cantoneiras em "L", vetorscópio graduado (ticks de 30°), barras de calibração dupla (SMPTE Color Bars), metadados do sinal ativos (formato, FPS, áudio) e Timecode SMPTE real contando quadros (:ff), ativo em qualquer cor de fundo.
- **Saída NDI tripla:** Mosaico horizontal, vertical e feed de áudio mixado.
- **Painel Web Widescreen Adaptativo:** Interface web responsiva com largura otimizada de até 1600px para aproveitar melhor as laterais de monitores widescreen e ultrawide.
- **SSE (Server-Sent Events)** para atualizações e telemetrias instantâneas de VU e hardware no painel web.
