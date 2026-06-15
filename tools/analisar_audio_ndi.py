"""
analisar_audio_ndi.py — Ferramenta de Análise de Telemetria de Áudio NDI

Esse script se conecta a uma fonte NDI (ex: feed do Teams ou gerador de teste)
e analisa a consistência, temporização e integridade dos pacotes de áudio recebidos.

Uso:
    python analisar_audio_ndi.py
"""

import os
import sys
import time
import numpy as np

try:
    import NDIlib as ndi
except ImportError:
    print("[ERRO] Pacote 'ndi-python' não encontrado.")
    print("       Instale com: pip install ndi-python")
    sys.exit(1)


def listar_fontes_ndi():
    """Descobre e lista as fontes NDI ativas na rede local."""
    find_instance = ndi.find_create_v2()
    if find_instance is None:
        print("[ERRO] Não foi possível criar o localizador de fontes NDI.")
        return []

    print("[*] Procurando fontes NDI na rede por 3 segundos...")
    time.sleep(3.0)

    sources = ndi.find_get_current_sources(find_instance)
    ndi.find_destroy(find_instance)
    return [s.ndi_name for s in sources]


def analisar_fonte(nome_fonte: str, duracao: float = 6.0):
    """Conecta à fonte e coleta telemetria dos frames de áudio recebidos."""
    print("=" * 60)
    print(f"  Análise de Áudio NDI para: {nome_fonte}")
    print("=" * 60)

    # Cria o receptor
    recv_settings = ndi.RecvCreateV3()
    recv_settings.color_format = ndi.RECV_COLOR_FORMAT_BGRX_BGRA
    recv_settings.bandwidth = ndi.RECV_BANDWIDTH_HIGHEST
    # Conecta especificamente à fonte desejada
    source = ndi.Source()
    source.ndi_name = nome_fonte
    recv_settings.source_to_connect_to = source

    ndi_recv = ndi.recv_create_v3(recv_settings)
    if ndi_recv is None:
        print(f"[ERRO] Não foi possível criar o receptor NDI para '{nome_fonte}'.")
        return

    # Warmup de 1 segundo para estabilizar a conexão
    print("[*] Conectando e estabilizando fluxo (1s)...")
    t_start = time.perf_counter()
    while time.perf_counter() - t_start < 1.0:
        t, v, a, m = ndi.recv_capture_v2(ndi_recv, 100)
        if t == ndi.FRAME_TYPE_VIDEO:
            ndi.recv_free_video_v2(ndi_recv, v)
        elif t == ndi.FRAME_TYPE_AUDIO:
            ndi.recv_free_audio_v2(ndi_recv, a)
        elif t == ndi.FRAME_TYPE_METADATA:
            ndi.recv_free_metadata(ndi_recv, m)

    print(f"[*] Capturando telemetria de áudio por {duracao} segundos...")

    eventos = []  # Armazena tuplas: (timestamp_local, no_samples, no_channels, sample_rate)
    cont_video = 0
    cont_metadata = 0
    cont_none = 0

    t_inicio_coleta = time.perf_counter()
    while time.perf_counter() - t_inicio_coleta < duracao:
        t, v, a, m = ndi.recv_capture_v2(ndi_recv, 50)  # Timeout curto

        agora = time.perf_counter()

        if t == ndi.FRAME_TYPE_AUDIO:
            eventos.append((agora, a.no_samples, a.no_channels, a.sample_rate))
            ndi.recv_free_audio_v2(ndi_recv, a)
        elif t == ndi.FRAME_TYPE_VIDEO:
            cont_video += 1
            ndi.recv_free_video_v2(ndi_recv, v)
        elif t == ndi.FRAME_TYPE_METADATA:
            cont_metadata += 1
            ndi.recv_free_metadata(ndi_recv, m)
        elif t == ndi.FRAME_TYPE_NONE:
            cont_none += 1

    ndi.recv_destroy(ndi_recv)
    print("[*] Coleta finalizada. Processando dados...")

    if not eventos:
        print("\n[AVISO] Nenhum frame de áudio foi recebido durante o teste!")
        print(f"        Frames de Vídeo recebidos: {cont_video}")
        return

    # Processamento Estatístico
    timestamps = np.array([e[0] for e in eventos])
    samples = np.array([e[1] for e in eventos])
    channels = np.array([e[2] for e in eventos])
    rates = np.array([e[3] for e in eventos])

    # 1. Delays de chegada (deltas de tempo entre frames sucessivos)
    deltas = np.diff(timestamps) * 1000.0  # Em milissegundos
    
    media_delta = np.mean(deltas) if len(deltas) > 0 else 0
    std_delta = np.std(deltas) if len(deltas) > 0 else 0  # Jitter de chegada
    min_delta = np.min(deltas) if len(deltas) > 0 else 0
    max_delta = np.max(deltas) if len(deltas) > 0 else 0

    # 2. Variação do tamanho do buffer de amostras
    tamanhos_unicos = np.unique(samples)
    taxas_unicas = np.unique(rates)
    canais_unicos = np.unique(channels)

    # 3. Taxa média real de amostras recebidas por segundo
    tempo_total = timestamps[-1] - timestamps[0]
    total_amostras = np.sum(samples)
    taxa_real_hz = total_amostras / tempo_total if tempo_total > 0 else 0
    taxa_nominal = rates[0]

    # Desvio de Clock (%)
    desvio_clock = ((taxa_real_hz - taxa_nominal) / taxa_nominal) * 100.0

    print("\n" + "=" * 60)
    print("                      RELATÓRIO DE ANÁLISE")
    print("=" * 60)
    print(f"Total de Frames de Áudio Coletados : {len(eventos)}")
    print(f"Total de Amostras Processadas      : {total_amostras}")
    print(f"Canais de Áudio Detectados         : {list(canais_unicos)} (Nominal: {canais_unicos[0] if len(canais_unicos) > 0 else 'N/A'})")
    print(f"Taxa de Amostragem Nominal         : {list(taxas_unicas)} Hz (Normalmente 48000)")
    print(f"Taxa de Amostragem Real (Calculada): {taxa_real_hz:.2f} Hz")
    print(f"Desvio do Relógio (Clock Drift)    : {desvio_clock:+.4f}%")

    print("\n--- Temporização de Chegada (Jitter) ---")
    print(f"Intervalo Médio entre Frames       : {media_delta:.2f} ms")
    print(f"Jitter de Chegada (Desvio Padrão)  : {std_delta:.2f} ms")
    print(f"Intervalo Mínimo                   : {min_delta:.2f} ms")
    print(f"Intervalo Máximo                   : {max_delta:.2f} ms")

    print("\n--- Tamanho dos Frames (no_samples) ---")
    print(f"Tamanhos de Frame Recebidos        : {list(tamanhos_unicos)} amostras")
    if len(tamanhos_unicos) > 1:
        print("  [ALERT] O tamanho das amostras de áudio varia dinamicamente!")
        for tam in tamanhos_unicos:
            qtd = np.sum(samples == tam)
            print(f"    - {tam} amostras: {qtd} vezes ({qtd/len(samples)*100:.1f}%)")
    else:
        print("  [OK] O tamanho das amostras de áudio é perfeitamente estável.")

    # Diagnóstico
    print("\n" + "=" * 60)
    print("                          DIAGNÓSTICO")
    print("=" * 60)

    diagnosticos = []

    # Diagnóstico de Jitter
    if std_delta > 10.0:
        diagnosticos.append(
            "[CRÍTICO] Alto Jitter de Rede detectado (" + f"{std_delta:.1f} ms" + ").\n"
            "          Os pacotes de áudio chegam muito instáveis. Se o buffer do mixer do software\n"
            "          for menor que " + f"{max_delta:.1f} ms" + ", haverá inevitavelmente esvaziamento\n"
            "          de buffer (underflow), causando estalos."
        )
    else:
        diagnosticos.append(
            "[OK] Jitter de Rede está baixo (" + f"{std_delta:.1f} ms" + ").\n"
            "     Os pacotes estão chegando em intervalos regulares e estáveis."
        )

    # Diagnóstico de Tamanho do Frame
    if len(tamanhos_unicos) > 1:
        diagnosticos.append(
            "[ATENÇÃO] O transmissor envia blocos de tamanhos variados.\n"
            "          O mixer de áudio precisa ser adaptável para lidar com buffers dinâmicos.\n"
            "          Se o mixer do painel espera sempre uma quantidade fixa (ex: 960), ele pode\n"
            "          gerar estalos ao preencher falhas com zeros."
        )
    else:
        diagnosticos.append(
            "[OK] O transmissor envia blocos de tamanho fixo (" + f"{tamanhos_unicos[0]}" + " amostras).\n"
            "     Isso simplifica a sincronização do mixer."
        )

    # Diagnóstico de Clock Drift
    if abs(desvio_clock) > 0.1:
        diagnosticos.append(
            "[ATENÇÃO] Desvio de Relógio (Clock Drift) relevante detectado (" + f"{desvio_clock:+.3f}%" + ").\n"
            "          O transmissor envia mais/menos amostras do que a taxa real. Sem sincronização\n"
            "          de clock ativa (reamostragem adaptativa), haverá sobrecarga ou falta de dados."
        )
    else:
        diagnosticos.append(
            "[OK] Desvio de relógio desprezível (" + f"{desvio_clock:+.3f}%" + ").\n"
            "     O transmissor está perfeitamente alinhado com o clock do sistema."
        )

    for d in diagnosticos:
        print(d)
        print("-" * 60)


def main():
    if not ndi.initialize():
        print("[ERRO FATAL] Falha ao inicializar o NDI SDK.")
        sys.exit(1)

    print("=" * 60)
    print("  Ferramenta de Diagnóstico de Áudio NDI")
    print("=" * 60)

    fontes = listar_fontes_ndi()
    if not fontes:
        print("\n[!] Nenhuma fonte NDI encontrada na rede.")
        print("    Certifique-se de que o Teams (com NDI ativo) ou o 'gerador_ndi.py' estão rodando.")
        ndi.destroy()
        sys.exit(0)

    print("\nFontes NDI disponíveis:")
    for idx, fonte in enumerate(fontes):
        print(f"  [{idx}] {fonte}")

    try:
        escolha = input(f"\nEscolha o número da fonte para analisar (0 a {len(fontes)-1}): ")
        idx_escolhido = int(escolha)
        if idx_escolhido < 0 or idx_escolhido >= len(fontes):
            raise ValueError()
    except (ValueError, KeyboardInterrupt):
        print("[*] Seleção inválida ou cancelada. Encerrando.")
        ndi.destroy()
        sys.exit(1)

    nome_selecionado = fontes[idx_escolhido]
    
    try:
        analisar_fonte(nome_selecionado)
    except KeyboardInterrupt:
        print("\n[*] Análise interrompida pelo usuário.")

    ndi.destroy()
    print("\n[*] Diagnóstico encerrado.")


if __name__ == "__main__":
    main()
