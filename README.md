# NDI Director

Sistema de controle e composição de vídeo NDI em tempo real, com painel web para gerenciamento de fontes, gravação individual por GPU (NVENC), e saída de mosaico NDI.

## Requisitos

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (Windows)
- [NDI Runtime](https://ndi.video/tools/) instalado na máquina
- [FFmpeg](https://ffmpeg.org/download.html) no PATH (para gravação)
- GPU NVIDIA com drivers atualizados (opcional, para codificação NVENC acelerada)

## Estrutura do Projeto

```
NDI Director/
├── src/                        # Código-fonte C# (.NET 8)
│   ├── NdiDirector.csproj
│   ├── Program.cs
│   └── Properties/
├── web/                        # Interface web (HTML)
│   └── templates/
│       ├── painel.html         # Painel principal de controle
│       └── dock.html           # Dock compacto para OBS
├── assets/                     # Recursos estáticos
│   └── ANTON-REGULAR.TTF       # Fonte para lower-thirds (GC)
├── tools/                      # Ferramentas de teste
│   ├── gerador_ndi.py          # Gerador de feeds NDI a partir de vídeos
│   └── requirements.txt
├── dist/                       # Build de distribuição (gerado)
├── .github/workflows/build.yml # Workflow do GitHub Actions
├── .gitignore
└── README.md
```

## Como Executar (Desenvolvimento)

```bash
cd src
dotnet run
```

O servidor web será iniciado na porta **8634**. Acesse:
- **Painel principal:** http://localhost:8634/
- **Dock OBS:** http://localhost:8634/dock

## Como Gerar o Build de Distribuição

```bash
cd src
dotnet publish -c Release -o ../dist
```

A pasta `dist/` conterá todos os arquivos necessários para executar em qualquer máquina Windows com .NET 8 Runtime instalado. Basta copiar a pasta inteira e executar `NdiDirector.exe`.

## Gerador de Feeds NDI (Testes)

Para simular fontes NDI usando arquivos de vídeo:

```bash
cd tools
pip install -r requirements.txt
python gerador_ndi.py
```

Coloque seus vídeos de teste (`.mp4`, `.webm`, `.mkv`, etc.) dentro da pasta `tools/`. O script criará automaticamente um feed NDI para cada vídeo encontrado.

> **Nota:** Os vídeos de teste são ignorados pelo `.gitignore` por serem arquivos grandes.

## CI/CD (GitHub Actions)

O projeto inclui uma esteira automática do GitHub Actions em `.github/workflows/build.yml`.

Sempre que você fizer um push para a branch `main` ou criar uma Pull Request, o GitHub irá:
1. Compilar o projeto.
2. Gerar a pasta de publicação `dist/` contendo o executável, templates HTML e fontes.
3. Disponibilizar a pasta `dist/` como um **artefato zipado** na aba **Actions** para download direto.

## Funcionalidades

- **Descoberta automática** de fontes NDI na rede local
- **Mosaico de até 4 câmeras** com layout automático (horizontal/vertical)
- **Highlight e Solo** para destaque de participantes
- **Gravação individual** por participante com FFmpeg + NVENC (GPU)
- **Mixer de áudio** em tempo real com controle de volume por fonte
- **Lower-thirds (GC)** com fonte customizada e apelidos editáveis
- **Saída NDI tripla**: mosaico horizontal, vertical e áudio mixado
- **SSE (Server-Sent Events)** para atualização em tempo real do painel web
