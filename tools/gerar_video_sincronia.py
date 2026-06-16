import os
import math
import struct
import wave
import subprocess
import numpy as np
import cv2

def gerar_video_teste():
    tools_dir = os.path.dirname(os.path.abspath(__file__))
    video_temp = os.path.join(tools_dir, "temp_video.mp4")
    audio_temp = os.path.join(tools_dir, "temp_audio.wav")
    saida_final = os.path.join(tools_dir, "video_teste_sincronia.mp4")
    
    print("[*] Iniciando a geracao do video de teste de sincronia...")
    
    fps = 30
    duracao_segundos = 30
    total_frames = fps * duracao_segundos
    largura = 1280
    altura = 720
    sample_rate = 48000
    amostras_por_frame = sample_rate // fps # 1600 amostras por frame a 30 FPS
    
    # 1. GERAR VÍDEO TEMPORÁRIO (MUDO) COM OPENCV
    fourcc = cv2.VideoWriter_fourcc(*'mp4v')
    video_writer = cv2.VideoWriter(video_temp, fourcc, float(fps), (largura, altura))
    
    if not video_writer.isOpened():
        print("[!] Erro: Nao foi possivel abrir o OpenCV VideoWriter.")
        return
        
    for frame_idx in range(total_frames):
        eh_flash = (frame_idx % fps == 0)
        
        if eh_flash:
            # Tela branca no flash
            img = np.ones((altura, largura, 3), dtype=np.uint8) * 255
            # Desenha círculo vermelho grande no centro
            cv2.circle(img, (largura // 2, altura // 2), 120, (0, 0, 255), -1)
        else:
            # Fundo cinza escuro
            img = np.ones((altura, largura, 3), dtype=np.uint8) * 35
            
            # Desenha círculo do mostrador analógico de frames
            centro_x, centro_y = largura // 2, altura // 2
            raio_relogio = 140
            cv2.circle(img, (centro_x, centro_y), raio_relogio, (80, 80, 80), 2)
            
            # Desenha as 30 divisões de frames ao redor do círculo
            for f in range(fps):
                angulo = math.radians(f * (360 / fps) - 90)
                x1 = int(centro_x + (raio_relogio - 10) * math.cos(angulo))
                y1 = int(centro_y + (raio_relogio - 10) * math.sin(angulo))
                x2 = int(centro_x + raio_relogio * math.cos(angulo))
                y2 = int(centro_y + raio_relogio * math.sin(angulo))
                
                cor = (0, 0, 255) if f == 0 else (120, 120, 120)
                espessura = 2 if f == 0 else 1
                cv2.line(img, (x1, y1), (x2, y2), cor, espessura)
                
            # Desenha o ponteiro giratório (posição do frame atual)
            frame_atual_ciclo = frame_idx % fps
            angulo_ponteiro = math.radians(frame_atual_ciclo * (360 / fps) - 90)
            px = int(centro_x + (raio_relogio - 20) * math.cos(angulo_ponteiro))
            py = int(centro_y + (raio_relogio - 20) * math.sin(angulo_ponteiro))
            cv2.line(img, (centro_x, centro_y), (px, py), (0, 255, 0), 3)
            cv2.circle(img, (centro_x, centro_y), 6, (0, 255, 0), -1)
            
        # Textos informativos
        segundos = frame_idx // fps
        resto_frames = frame_idx % fps
        tempo_str = f"{segundos:02d}s {resto_frames:02d}f"
        
        # Desenha o tempo e frame atual
        cor_texto = (0, 0, 0) if eh_flash else (255, 255, 255)
        cv2.putText(img, tempo_str, (largura // 2 - 120, altura // 2 + 220),
                    cv2.FONT_HERSHEY_SIMPLEX, 1.6, cor_texto, 4, cv2.LINE_AA)
        
        # Desenha títulos explicativos
        cv2.putText(img, "TESTE DE SINCRONIA AV - NDI DIRECTOR", (50, 60),
                    cv2.FONT_HERSHEY_SIMPLEX, 1.0, (0, 200, 255) if not eh_flash else (0, 0, 255), 2, cv2.LINE_AA)
        cv2.putText(img, "Fundo Branco + Beep = Sincronia Zero (Frame 00)", (50, 100),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.7, (180, 180, 180) if not eh_flash else (80, 80, 80), 1, cv2.LINE_AA)
        
        video_writer.write(img)
        
    video_writer.release()
    print("[*] Video temporario (mudo) gerado.")
    
    # 2. GERAR ÁUDIO TEMPORÁRIO (WAV) COM BIPE SINCRONIZADO
    # 48000 Hz, stereo, 16-bit
    frequencia_som = 1000.0  # 1kHz
    
    with wave.open(audio_temp, 'wb') as wav_file:
        wav_file.setnchannels(2)
        wav_file.setsampwidth(2) # 16-bit
        wav_file.setframerate(sample_rate)
        
        for frame_idx in range(total_frames):
            eh_flash = (frame_idx % fps == 0)
            
            if eh_flash:
                # Beep de 1000Hz durante o exato frame (1600 amostras)
                t = np.arange(amostras_por_frame) / float(sample_rate)
                # Aplica um pequeno fade-in/fade-out de 3ms para evitar estalo fisico no bipe
                som = 0.5 * np.sin(2 * np.pi * frequencia_som * t)
                
                # Fade-in/out rápido (150 amostras ~ 3ms)
                fade_len = min(150, amostras_por_frame // 2)
                for i in range(fade_len):
                    fator_in = i / float(fade_len)
                    fator_out = 1.0 - fator_in
                    som[i] *= fator_in
                    som[-1 - i] *= fator_in
            else:
                # Silêncio
                som = np.zeros(amostras_por_frame)
                
            som_int16 = (som * 32767).astype(np.int16)
            
            # Conversão para bytes estruturados stereo interleaved
            data_packed = bytearray()
            for s in som_int16:
                data_packed.extend(struct.pack('<hh', s, s))
                
            wav_file.writeframes(data_packed)
            
    print("[*] Audio temporario (WAV) gerado.")
    
    # 3. MUXING COM FFMPEG PARA GERAR O MP4 FINAL
    print("[*] Combinando audio e video com FFmpeg...")
    cmd = [
        "ffmpeg", "-y",
        "-i", video_temp,
        "-i", audio_temp,
        "-c:v", "libx264", "-pix_fmt", "yuv420p", "-preset", "fast", "-crf", "18",
        "-c:a", "aac", "-b:a", "192k",
        saida_final
    ]
    
    try:
        resultado = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, check=True)
        print("[*] FFmpeg concluiu o muxing com sucesso.")
        
        # Limpeza
        if os.path.exists(video_temp):
            os.remove(video_temp)
        if os.path.exists(audio_temp):
            os.remove(audio_temp)
            
        print(f"[+] Video de teste criado com sucesso em: {saida_final}")
    except subprocess.CalledProcessError as e:
        print(f"[!] Erro ao rodar FFmpeg: {e.stderr}")
    except Exception as ex:
        print(f"[!] Erro inesperado: {ex}")

if __name__ == "__main__":
    gerar_video_teste()
