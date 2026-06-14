"""
gerador_ndi.py — Gerador de Feed NDI a partir de Vídeo

Cria um feed NDI a partir de um arquivo de vídeo, com áudio e vídeo.
O vídeo é reproduzido em loop contínuo até o script ser encerrado (Ctrl+C).

Para múltiplos feeds, execute várias instâncias do script em terminais separados.

Uso:
    python gerador_ndi.py                    # usa o primeiro vídeo encontrado na pasta
    python gerador_ndi.py meu_video.mp4      # usa o vídeo especificado

Dependências:
    pip install ndi-python av numpy
"""

import os
import sys
import time
import signal
import numpy as np
import av

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
    nome = nome.replace("_", " ").replace("-", " ").strip()
    return f"NDI Test - {nome}"


def enviar_feed_ndi(caminho_video: str):
    """
    Abre o vídeo com PyAV e envia cada frame de áudio e vídeo como um feed NDI.
    O vídeo é reproduzido em loop contínuo até a flag _rodando ser False.
    """
    global _rodando

    nome_ndi = nome_fonte_ndi(caminho_video)
    nome_arquivo = os.path.basename(caminho_video)

    # Cria o sender NDI
    send_settings = ndi.SendCreate()
    send_settings.ndi_name = nome_ndi
    send_settings.clock_video = False
    send_settings.clock_audio = False

    sender = ndi.send_create(send_settings)
    if sender is None:
        print(f"[ERRO] Falha ao criar sender NDI para: {nome_ndi}")
        return

    print(f"[+] Feed NDI criado: '{nome_ndi}' <- {nome_arquivo}")

    loop_count = 0

    while _rodando:
        try:
            container = av.open(caminho_video)
        except Exception as e:
            print(f"[ERRO] Não foi possível abrir: {nome_arquivo}. Erro: {e}")
            break

        # Streams de vídeo e áudio
        video_stream = container.streams.video[0] if container.streams.video else None
        audio_stream = container.streams.audio[0] if container.streams.audio else None

        if not video_stream:
            print(f"[ERRO] Nenhum stream de vídeo encontrado em: {nome_arquivo}")
            container.close()
            break

        # Obtém FPS do vídeo de forma robusta
        fps = 30.0
        if video_stream.average_rate is not None:
            try:
                fps = float(video_stream.average_rate)
            except (ValueError, TypeError):
                pass

        if fps <= 0 or fps > 120 or video_stream.average_rate is None:
            for attr in ['guessed_rate', 'base_rate', 'r_frame_rate']:
                val = getattr(video_stream, attr, None)
                if val is not None:
                    try:
                        f_val = float(val)
                        if 0 < f_val <= 120:
                            fps = f_val
                            break
                    except (ValueError, TypeError):
                        pass

        largura = video_stream.width
        altura = video_stream.height

        if loop_count == 0:
            print(f"    Resolução: {largura}x{altura} @ {fps:.1f} FPS")
            if audio_stream:
                print(f"    Áudio: {audio_stream.sample_rate}Hz, {audio_stream.channels} canais")
            else:
                print("    Áudio: Nenhum stream de áudio detectado")
            print(f"\n[*] Feed NDI ativo. Pressione Ctrl+C para encerrar.\n")

        # Configura o resampler de áudio se houver áudio
        resampler = None
        if audio_stream:
            try:
                resampler = av.AudioResampler(
                    format='fltp',
                    layout='stereo',
                    rate=48000
                )
            except Exception as e:
                print(f"[AVISO] Falha ao criar resampler de áudio: {e}. Áudio desativado.")
                resampler = None

        start_time = time.perf_counter()
        video_frame_count = 0
        first_pts = None
        sent_buffers_keepalive = []

        try:
            for frame in container.decode(video=0, audio=0 if audio_stream else None):
                if not _rodando:
                    break

                if isinstance(frame, av.VideoFrame):
                    # Sincronização temporal baseada em PTS
                    pts_segundos = None
                    if frame.pts is not None and video_stream.time_base is not None:
                        raw_pts = float(frame.pts * video_stream.time_base)
                        if first_pts is None:
                            first_pts = raw_pts
                        pts_segundos = raw_pts - first_pts

                    if pts_segundos is None or pts_segundos < 0:
                        pts_segundos = video_frame_count / fps

                    video_frame_count += 1

                    tempo_decorrido = time.perf_counter() - start_time
                    delay = pts_segundos - tempo_decorrido
                    if delay > 0:
                        time.sleep(min(delay, 1.0))

                    # Converte frame PyAV → numpy array BGRA contíguo
                    frame_bgra = frame.to_ndarray(format='bgra')
                    if not frame_bgra.flags["C_CONTIGUOUS"]:
                        frame_bgra = np.ascontiguousarray(frame_bgra)

                    # Envia o frame de vídeo NDI
                    video_frame = ndi.VideoFrameV2()
                    video_frame.data = frame_bgra
                    video_frame.FourCC = ndi.FOURCC_VIDEO_TYPE_BGRX
                    video_frame.xres = largura
                    video_frame.yres = altura

                    ndi.send_send_video_v2(sender, video_frame)

                    # Mantém o buffer vivo na memória
                    sent_buffers_keepalive.append((video_frame, frame_bgra))
                    if len(sent_buffers_keepalive) > 30:
                        sent_buffers_keepalive.pop(0)

                elif isinstance(frame, av.AudioFrame) and resampler:
                    resampled_frames = resampler.resample(frame)
                    for res_frame in resampled_frames:
                        audio_data = res_frame.to_ndarray()  # Shape: (2, no_samples)

                        # Array float32 planar contíguo → view uint8
                        audio_planar_flat = np.ascontiguousarray(
                            audio_data.astype(np.float32).flatten()
                        )
                        audio_bytes_array = audio_planar_flat.view(np.uint8)

                        # Envia o frame de áudio NDI
                        audio_frame = ndi.AudioFrameV2()
                        audio_frame.sample_rate = 48000
                        audio_frame.no_channels = 2
                        audio_frame.no_samples = res_frame.samples
                        audio_frame.channel_stride_in_bytes = res_frame.samples * 4
                        audio_frame.data = audio_bytes_array

                        ndi.send_send_audio_v2(sender, audio_frame)

                        # Mantém o buffer vivo na memória
                        sent_buffers_keepalive.append((audio_frame, audio_bytes_array, audio_planar_flat))
                        if len(sent_buffers_keepalive) > 60:
                            sent_buffers_keepalive.pop(0)

            # Flush do resampler
            if _rodando and resampler:
                resampled_frames = resampler.resample(None)
                for res_frame in resampled_frames:
                    audio_data = res_frame.to_ndarray()
                    audio_planar_flat = np.ascontiguousarray(
                        audio_data.astype(np.float32).flatten()
                    )
                    audio_bytes_array = audio_planar_flat.view(np.uint8)

                    audio_frame = ndi.AudioFrameV2()
                    audio_frame.sample_rate = 48000
                    audio_frame.no_channels = 2
                    audio_frame.no_samples = res_frame.samples
                    audio_frame.channel_stride_in_bytes = res_frame.samples * 4
                    audio_frame.data = audio_bytes_array

                    ndi.send_send_audio_v2(sender, audio_frame)

                    sent_buffers_keepalive.append((audio_frame, audio_bytes_array, audio_planar_flat))
                    if len(sent_buffers_keepalive) > 60:
                        sent_buffers_keepalive.pop(0)

        except Exception as e:
            import traceback
            print(f"[ERRO] Falha durante a decodificação do vídeo {nome_arquivo}: {e}")
            traceback.print_exc()

        container.close()
        loop_count += 1

        if _rodando:
            print(f"[*] Loop {loop_count} finalizado. Reiniciando vídeo...")
            time.sleep(0.05)

    ndi.send_destroy(sender)
    print(f"[-] Feed NDI encerrado: '{nome_ndi}'")


def main():
    global _rodando

    print("=" * 60)
    print("  NDI Director — Gerador de Feed NDI para Testes")
    print("=" * 60)
    print()

    # Inicializa o NDI SDK
    if not ndi.initialize():
        print("[ERRO FATAL] Falha ao inicializar o NDI SDK.")
        print("             Verifique se o NDI Runtime está instalado.")
        sys.exit(1)

    print("[*] NDI SDK inicializado com sucesso.")

    pasta_script = os.path.dirname(os.path.abspath(__file__))

    # Determina o vídeo a usar
    if len(sys.argv) > 1:
        # Vídeo passado como argumento
        caminho_video = sys.argv[1]
        if not os.path.isabs(caminho_video):
            caminho_video = os.path.join(os.getcwd(), caminho_video)
        if not os.path.isfile(caminho_video):
            print(f"[ERRO] Arquivo não encontrado: {caminho_video}")
            ndi.destroy()
            sys.exit(1)
    else:
        # Usa o primeiro vídeo encontrado na pasta do script
        videos = listar_videos(pasta_script)
        if not videos:
            print(f"\n[!] Nenhum arquivo de vídeo encontrado em: {pasta_script}")
            print(f"    Extensões suportadas: {', '.join(sorted(EXTENSOES_VIDEO))}")
            print(f"    Coloque seus vídeos de teste na mesma pasta deste script.")
            print(f"    Ou passe o caminho do vídeo como argumento:")
            print(f"    python gerador_ndi.py caminho/para/video.mp4")
            ndi.destroy()
            sys.exit(0)
        caminho_video = videos[0]
        if len(videos) > 1:
            print(f"\n[*] {len(videos)} vídeo(s) encontrado(s). Usando o primeiro:")
            for i, v in enumerate(videos):
                marcador = "  >" if i == 0 else "   "
                print(f"    {marcador} {os.path.basename(v)}")
            print(f"\n    Para usar outro, passe como argumento:")
            print(f"    python gerador_ndi.py nome_do_video.mp4\n")

    print(f"\n[*] Vídeo selecionado: {os.path.basename(caminho_video)}")

    # Configura handler para encerramento limpo (Ctrl+C)
    def handler_sinal(sig, frame):
        global _rodando
        if _rodando:
            print("\n[*] Encerrando feed NDI...")
            _rodando = False

    signal.signal(signal.SIGINT, handler_sinal)
    signal.signal(signal.SIGTERM, handler_sinal)

    # Executa diretamente (sem threading)
    enviar_feed_ndi(caminho_video)

    ndi.destroy()
    print("[*] NDI SDK finalizado. Até mais!")


if __name__ == "__main__":
    main()