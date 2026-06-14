"""
gerador_ndi.py — Gerador de Feeds NDI a partir de Vídeos

Escaneia a pasta onde este script está localizado em busca de arquivos de vídeo
e cria um feed NDI independente para cada arquivo encontrado.
Os vídeos são reproduzidos em loop contínuo até o script ser encerrado (Ctrl+C).

Uso:
    python gerador_ndi.py

Dependências:
    pip install ndi-python opencv-python numpy
"""

import os
import sys
import time
import signal
import threading
import numpy as np
import cv2

try:
    import NDIlib as ndi
except ImportError:
    print("[ERRO] Pacote 'ndi-python' não encontrado.")
    print("       Instale com: pip install ndi-python")
    sys.exit(1)

# Extensões de vídeo suportadas
EXTENSOES_VIDEO = {".mp4", ".mkv", ".webm", ".avi", ".mov", ".wmv", ".flv", ".ts", ".m4v"}

# Flag global para encerramento limpo
_rodando = True


def listar_videos(pasta: str) -> list[str]:
    """Retorna a lista de caminhos absolutos de vídeos encontrados na pasta."""
    videos = []
    for arquivo in sorted(os.listdir(pasta)):
        ext = os.path.splitext(arquivo)[1].lower()
        if ext in EXTENSOES_VIDEO:
            caminho = os.path.join(pasta, arquivo)
            if os.path.isfile(caminho):
                videos.append(caminho)
    return videos


def nome_fonte_ndi(caminho_video: str) -> str:
    """Gera o nome do feed NDI a partir do nome do arquivo (sem extensão)."""
    nome = os.path.splitext(os.path.basename(caminho_video))[0]
    # Capitaliza e limpa para um nome apresentável
    nome = nome.replace("_", " ").replace("-", " ").strip()
    return f"NDI Test - {nome}"


def enviar_feed_ndi(caminho_video: str):
    """
    Abre o vídeo com OpenCV e envia cada frame como um feed NDI.
    O vídeo é reproduzido em loop contínuo até a flag _rodando ser False.
    """
    global _rodando

    nome_ndi = nome_fonte_ndi(caminho_video)
    nome_arquivo = os.path.basename(caminho_video)

    # Cria o sender NDI
    send_settings = ndi.SendCreate()
    send_settings.ndi_name = nome_ndi
    send_settings.clock_video = True
    send_settings.clock_audio = False

    sender = ndi.send_create(send_settings)
    if sender is None:
        print(f"[ERRO] Falha ao criar sender NDI para: {nome_ndi}")
        return

    print(f"[+] Feed NDI criado: '{nome_ndi}' ← {nome_arquivo}")

    # Frame NDI reutilizável
    video_frame = ndi.VideoFrameV2()

    loop_count = 0

    while _rodando:
        cap = cv2.VideoCapture(caminho_video)

        if not cap.isOpened():
            print(f"[ERRO] Não foi possível abrir: {nome_arquivo}")
            break

        # Obtém FPS do vídeo (com fallback para 30)
        fps = cap.get(cv2.CAP_PROP_FPS)
        if fps <= 0 or fps > 120:
            fps = 30.0

        largura = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
        altura = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
        total_frames = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))

        if loop_count == 0:
            print(f"    Resolução: {largura}x{altura} @ {fps:.1f} FPS ({total_frames} frames)")

        intervalo_frame = 1.0 / fps

        while _rodando:
            ret, frame_bgr = cap.read()

            if not ret:
                # Fim do vídeo, reinicia o loop
                break

            # Converte BGR → BGRA (formato esperado pelo NDI)
            frame_bgra = cv2.cvtColor(frame_bgr, cv2.COLOR_BGR2BGRA)

            # Garante que o array é contíguo em memória (C-contiguous)
            if not frame_bgra.flags["C_CONTIGUOUS"]:
                frame_bgra = np.ascontiguousarray(frame_bgra)

            # Configura o frame NDI
            video_frame.data = frame_bgra
            video_frame.FourCC = ndi.FOURCC_VIDEO_TYPE_BGRX

            # Envia o frame (clock_video=True faz o throttling automático no FPS correto)
            ndi.send_send_video_v2(sender, video_frame)

        cap.release()
        loop_count += 1

        if _rodando:
            # Pequena pausa entre loops para evitar micro-stutters na transição
            time.sleep(0.05)

    ndi.send_destroy(sender)
    print(f"[-] Feed NDI encerrado: '{nome_ndi}'")


def main():
    global _rodando

    print("=" * 60)
    print("  NDI Director — Gerador de Feeds NDI para Testes")
    print("=" * 60)
    print()

    # Inicializa o NDI SDK
    if not ndi.initialize():
        print("[ERRO FATAL] Falha ao inicializar o NDI SDK.")
        print("             Verifique se o NDI Runtime está instalado.")
        sys.exit(1)

    print("[*] NDI SDK inicializado com sucesso.")

    # Escaneia a pasta do script em busca de vídeos
    pasta_script = os.path.dirname(os.path.abspath(__file__))
    videos = listar_videos(pasta_script)

    if not videos:
        print(f"\n[!] Nenhum arquivo de vídeo encontrado em: {pasta_script}")
        print(f"    Extensões suportadas: {', '.join(sorted(EXTENSOES_VIDEO))}")
        print(f"    Coloque seus vídeos de teste na mesma pasta deste script.")
        ndi.destroy()
        sys.exit(0)

    print(f"\n[*] {len(videos)} vídeo(s) encontrado(s):\n")
    for v in videos:
        print(f"    • {os.path.basename(v)}")
    print()

    # Configura handler para encerramento limpo (Ctrl+C)
    def handler_sinal(sig, frame):
        global _rodando
        if _rodando:
            print("\n[*] Encerrando feeds NDI...")
            _rodando = False

    signal.signal(signal.SIGINT, handler_sinal)
    signal.signal(signal.SIGTERM, handler_sinal)

    # Inicia uma thread por vídeo
    threads = []
    for caminho_video in videos:
        t = threading.Thread(
            target=enviar_feed_ndi,
            args=(caminho_video,),
            daemon=True,
            name=f"NDI_{os.path.basename(caminho_video)}"
        )
        t.start()
        threads.append(t)
        # Pequeno delay entre inícios para evitar contenção no NDI SDK
        time.sleep(0.3)

    print(f"[*] Todos os {len(threads)} feeds NDI estão ativos. Pressione Ctrl+C para encerrar.\n")

    # Aguarda até Ctrl+C
    try:
        while _rodando:
            time.sleep(0.5)
    except KeyboardInterrupt:
        _rodando = False

    # Aguarda as threads encerrarem
    print("[*] Aguardando threads finalizarem...")
    for t in threads:
        t.join(timeout=5)

    ndi.destroy()
    print("[*] NDI SDK finalizado. Até mais!")


if __name__ == "__main__":
    main()
