const isDock = document.body.classList.contains('modo-dock');
const ultimosVolumes = {};
let livePreviewAtivo = true;

function carregarFontes() {
    const activeEl = document.activeElement;
    let focadoId = null, cursorStart = 0, cursorEnd = 0;

    if (activeEl && activeEl.classList.contains('input-name')) {
        focadoId   = activeEl.id;
        cursorStart = activeEl.selectionStart;
        cursorEnd   = activeEl.selectionEnd;
    }

    fetch('/api/fontes')
    .then(res => res.json())
    .then(dados => {
        const painel = document.getElementById('painel');

        if (dados.length === 0) {
            if (isDock) {
                painel.innerHTML = `<div class="empty-state">Buscando fontes NDI...</div>`;
            } else {
                painel.innerHTML = `
                    <div class="empty-state">
                        <p>Nenhuma fonte NDI encontrada na rede local...</p>
                        <p style="font-size:0.85rem;margin-top:8px;">Certifique-se de que os feeds NDI estão transmitindo na mesma subrede.</p>
                    </div>`;
            }
            return;
        }

        const emptyState = painel.querySelector('.empty-state');
        if (emptyState) emptyState.remove();

        dados.sort((a, b) => a.nome.localeCompare(b.nome));
        const idsAtuais = new Set();

        dados.forEach(fonte => {
            const cardId = 'card-' + fonte.nome.replace(/[^a-zA-Z0-9]/g, '_');
            idsAtuais.add(cardId);

            let card = document.getElementById(cardId);
            if (!card) {
                card = document.createElement('div');
                card.id = cardId;
                card.innerHTML = `
                    <div class="status-badge"><span class="status-dot"></span><span class="status-text"></span></div>
                    <div class="order-badge" style="display:none;"></div>
                    <div class="feed-title">${fonte.nome}</div>
                    <div class="dynamic-content"></div>
                    <div class="btn-group"></div>`;
                painel.appendChild(card);
            }

            card.className = `feed-card ${fonte.ativo ? 'active' : ''} ${fonte.highlight ? 'highlighted' : ''} ${fonte.solo ? 'solo' : ''} ${fonte.erro ? 'erro' : ''}`;

            // Garante que o ícone de status de rede esteja presente no card
            let netIcon = card.querySelector('.net-status-icon');
            if (!netIcon) {
                const badge = card.querySelector('.status-badge');
                if (badge) {
                    netIcon = document.createElement('span');
                    netIcon.className = 'net-status-icon';
                    netIcon.style.display = 'none';
                    netIcon.textContent = '📶';
                    badge.appendChild(netIcon);
                }
            }

            let statusText = '';
            if (isDock) {
                statusText = fonte.erro ? 'Recon...' : (fonte.ativo ? 'Na Cena' : 'Dispo');
            } else {
                statusText = fonte.erro ? 'Reconectando...' : (fonte.ativo ? 'Na Cena' : 'Disponível');
            }
            
            if (fonte.resolucao) {
                const fpsVal = fonte.fps ? (isDock ? Math.round(fonte.fps) : (fonte.fps % 1 === 0 ? fonte.fps : fonte.fps.toFixed(2))) : '';
                statusText += ` (${fonte.resolucao}${fpsVal ? ' @ ' + fpsVal + ' FPS' : ''})`;
            }
            card.querySelector('.status-text').textContent = statusText;

            const orderBadge = card.querySelector('.order-badge');
            if (fonte.ativo) {
                orderBadge.textContent    = fonte.posicao + 1;
                orderBadge.style.display  = 'flex';
            } else {
                orderBadge.style.display  = 'none';
            }

            const dynamicContent = card.querySelector('.dynamic-content');
            const inputId = `input-name-${cardId}`;

            let inputEl = document.getElementById(inputId);
            if (!inputEl) {
                const previewHeight = isDock ? 75 : 118;
                const gcLabel = isDock ? 'Nome do Participante (GC):' : 'Nome do participante (GC):';
                const gcPlaceholder = isDock ? 'Ex: João - @joao' : 'Ex: João Silva - @joaosilva';
                
                dynamicContent.innerHTML = `
                    <div class="preview-wrapper">
                        <div class="recording-timer-badge ${fonte.gravando ? '' : 'oculto'}" data-desde="${fonte.gravando_desde || ''}">00:00</div>
                        <img class="feed-preview"
                             src="/api/preview/${encodeURIComponent(fonte.nome)}?t=${Date.now()}"
                             alt="Preview"
                             onerror="this.classList.add('oculto')" />
                        <button class="btn-refresh-preview" onclick="refreshPreview(this,'${fonte.nome}')" title="Atualizar preview">🔄</button>
                    </div>
                    <div class="name-editor">
                        <span class="name-editor-label">${gcLabel}</span>
                        <div class="name-editor-row">
                            <input type="text" class="input-name" placeholder="${gcPlaceholder}"
                                 value="${fonte.apelido || ''}" id="${inputId}"
                                 onblur="autoSalvarApelido(this, '${fonte.nome}')"
                                 onkeydown="if(event.key==='Enter'){event.preventDefault();salvarApelido(this.parentElement.querySelector('.btn-save-name'),'${fonte.nome}','${inputId}');}" />
                            <button class="btn-save-name" onclick="salvarApelido(this,'${fonte.nome}','${inputId}')">Salvar</button>
                        </div>
                    </div>`;
            } else if (inputId !== focadoId) {
                inputEl.value = fonte.apelido || '';
            }

            const timerBadge = card.querySelector('.recording-timer-badge');
            if (timerBadge) {
                if (fonte.gravando) {
                    timerBadge.classList.remove('oculto');
                    if (fonte.gravando_desde) {
                        timerBadge.setAttribute('data-desde', fonte.gravando_desde);
                    }
                } else {
                    timerBadge.classList.add('oculto');
                    timerBadge.removeAttribute('data-desde');
                    timerBadge.textContent = '00:00';
                }
            }

            let gravarHtml = '';
            if (fonte.muxing) {
                const m = fonte.muxing;
                let label = `Processando: ${m.progresso}%`;
                let containerClass = '';
                if (m.concluido) {
                    label = 'Concluído! 🎉';
                    containerClass = 'concluido';
                } else if (m.erro) {
                    label = `Erro: ${m.erro}`;
                    containerClass = 'erro';
                }
                
                gravarHtml = `
                    <div class="mux-progress-container ${containerClass}">
                        <div class="mux-progress-label">
                            <span>Muxing Áudio/Vídeo</span>
                            <span>${label}</span>
                        </div>
                        <div class="mux-progress-bar-bg">
                            <div class="mux-progress-bar-fill" style="width: ${m.erro ? 100 : m.progresso}%"></div>
                        </div>
                    </div>
                `;
            } else {
                const btnGravarText = fonte.gravando 
                    ? (isDock ? '🔴 Parar Gravação' : '🔴 Gravando (Parar)') 
                    : '⏺️ Gravar Feed (GPU)';
                
                gravarHtml = `
                    <button class="btn btn-gravar ${fonte.gravando ? 'gravando' : ''}" onclick="toggleGravar('${fonte.nome}', ${fonte.gravando})">
                        <span>${btnGravarText}</span>
                    </button>
                `;
            }

            const btnGroup = card.querySelector('.btn-group');
            
            const volumeHtml = fonte.ativo ? `
                <div class="volume-selector">
                    <div style="display: flex; justify-content: space-between; align-items: center; margin-bottom: ${isDock ? 5 : 6}px;">
                        <span style="font-size: ${isDock ? 0.7 : 0.75}rem; color: var(--text-muted); font-weight: 600;">Ganho do Áudio:</span>
                        <span class="volume-value" id="vol-value-${cardId}" ondblclick="restaurarVolumePadrao('${fonte.nome}', '${cardId}')" style="font-size: ${isDock ? 0.7 : 0.75}rem; font-weight: 700; color: var(--text-main); font-family: monospace; cursor: pointer;" title="Duplo clique para redefinir para 100%">${fonte.volume}%</span>
                    </div>
                    <div class="vu-meter-container ${fonte.volume === 0 ? 'muted' : ''}" title="VU de Áudio">
                        <div class="vu-meter-mask" id="vu-mask-${cardId}" style="width: 100%;"></div>
                    </div>
                    <div style="display: flex; align-items: center; gap: ${isDock ? 8 : 10}px;">
                        <button class="btn-mute" id="btn-mute-${cardId}" onclick="toggleMute('${fonte.nome}', '${cardId}')" title="Mute/Unmute">
                            ${fonte.volume === 0 ? '🔇' : '🔊'}
                        </button>
                        <input type="range" min="0" max="150" value="${fonte.volume}" class="volume-slider" id="slider-${cardId}" oninput="atualizarVolumeVisual('${fonte.nome}', this.value, '${cardId}')" onchange="alterarVolume('${fonte.nome}', this.value, '${cardId}')" ondblclick="restaurarVolumePadrao('${fonte.nome}', '${cardId}')" title="Duplo clique para redefinir para 100%" />
                    </div>
                </div>
            ` : '';

            const highlightText = fonte.highlight 
                ? (isDock ? '⭐ Remover' : '⭐ Em Destaque (Remover)') 
                : '⭐ Destaque';
            
            const soloText = fonte.solo 
                ? (isDock ? '🎯 Tirar' : '🎯 Solo (Remover)') 
                : '🎯 Solo';

            let botoesLayoutHtml = '';
            if (isDock) {
                botoesLayoutHtml = `
                    <div class="btn-row">
                        <button class="btn btn-highlight" onclick="toggleHighlight('${fonte.nome}')" ${fonte.ativo ? '' : 'disabled'}>
                            <span>${highlightText}</span>
                        </button>
                        <button class="btn btn-solo" onclick="toggleSolo('${fonte.nome}')" ${fonte.ativo ? '' : 'disabled'}>
                            <span>${soloText}</span>
                        </button>
                    </div>
                `;
            } else {
                botoesLayoutHtml = `
                    <button class="btn btn-highlight" onclick="toggleHighlight('${fonte.nome}')" ${fonte.ativo ? '' : 'disabled'}>
                        <span>${highlightText}</span>
                    </button>
                    <button class="btn btn-solo" onclick="toggleSolo('${fonte.nome}')" ${fonte.ativo ? '' : 'disabled'}>
                        <span>${soloText}</span>
                    </button>
                `;
            }

            const btnsHtml = `
                ${botoesLayoutHtml}
                ${gravarHtml}
                ${volumeHtml}
                <div class="pos-selector">
                    <span>Posição${isDock ? '' : ' no'} Mosaico:</span>
                    <div class="pos-buttons">
                        <button class="btn-pos ${fonte.posicao===0?'active':''}" onclick="mudarPosicao('${fonte.nome}',0)">1</button>
                        <button class="btn-pos ${fonte.posicao===1?'active':''}" onclick="mudarPosicao('${fonte.nome}',1)">2</button>
                        <button class="btn-pos ${fonte.posicao===2?'active':''}" onclick="mudarPosicao('${fonte.nome}',2)">3</button>
                        <button class="btn-pos ${fonte.posicao===3?'active':''}" onclick="mudarPosicao('${fonte.nome}',3)">4</button>
                    </div>
                </div>
                ${fonte.ativo ? `
                    <button class="btn btn-remove" onclick="toggle(this,'${fonte.nome}')">
                        <span>❌ Remover da Cena</span>
                    </button>
                ` : `
                    <button class="btn btn-add" onclick="toggle(this,'${fonte.nome}')">
                        <span>➕ Adicionar à Cena</span>
                    </button>
                `}`;

            if (btnGroup.innerHTML !== btnsHtml) btnGroup.innerHTML = btnsHtml;
        });

        painel.querySelectorAll('.feed-card').forEach(card => {
            if (!idsAtuais.has(card.id)) card.remove();
        });

        if (focadoId) {
            const el = document.getElementById(focadoId);
            if (el) { el.focus(); el.setSelectionRange(cursorStart, cursorEnd); }
        }
    });
}

function toggle(btn, nome) {
    const cardId = 'card-' + nome.replace(/[^a-zA-Z0-9]/g, '_');
    const card   = document.getElementById(cardId);
    const isActive = card && card.classList.contains('active');

    if (card) {
        card.querySelectorAll('button').forEach(b => b.disabled = true);
        const st = card.querySelector('.status-text');
        if (st) st.textContent = isActive ? 'Removendo...' : 'Conectando...';
        if (isActive) card.classList.remove('active','highlighted','erro');
        else          card.classList.add('active');
    }

    fetch('/toggle/' + encodeURIComponent(nome), { method: 'POST' })
    .then(res => { if (!res.ok) return res.json().then(e => { alert(e.message); carregarFontes(); }); })
    .catch(() => carregarFontes());
}

function abrirConfiguracoes() {
    carregarConfiguracoes();
    const modal = document.getElementById('modal-configuracoes');
    modal.style.display = 'flex';
    setTimeout(() => modal.classList.add('show'), 10);
}

function fecharConfiguracoes() {
    const modal = document.getElementById('modal-configuracoes');
    modal.classList.remove('show');
    setTimeout(() => modal.style.display = 'none', 300);
}

// Fecha o modal ao clicar fora dele
window.addEventListener('click', (event) => {
    const modal = document.getElementById('modal-configuracoes');
    if (event.target === modal) {
        fecharConfiguracoes();
    }
});

function carregarConfiguracoes() {
    fetch('/api/configuracoes')
    .then(res => res.json())
    .then(dados => {
        // Atualiza seletor de áudio
        document.querySelectorAll('.btn-format').forEach(btn => btn.classList.remove('active'));
        const btnAudio = document.getElementById('btn-audio-' + dados.formatoAudio);
        if (btnAudio) btnAudio.classList.add('active');

        // Atualiza seletor de fundo do mosaico
        document.querySelectorAll('.btn-color-modal').forEach(btn => btn.classList.remove('active'));
        const btnFundo = document.querySelector(`.bg-buttons-modal .btn-${dados.corFundo}`);
        if (btnFundo) btnFundo.classList.add('active');

        // Atualiza seletor de qualidade
        document.querySelectorAll('.btn-quality').forEach(btn => btn.classList.remove('active'));
        const btnQuality = document.getElementById('btn-quality-' + dados.qualidadeGravacao);
        if (btnQuality) btnQuality.classList.add('active');

        // Atualiza checkbox de temporários
        const chkTemporarios = document.getElementById('chk-apagar-temporarios');
        if (chkTemporarios) chkTemporarios.checked = dados.apagarTemporarios;

        // Atualiza checkbox de diagnóstico
        const chkDiagnostico = document.getElementById('chk-logs-diagnostico');
        if (chkDiagnostico) chkDiagnostico.checked = dados.habilitarLogsDiagnostico;

        // Atualiza checkbox de Live Preview
        const chkLivePreview = document.getElementById('chk-live-preview');
        if (chkLivePreview) {
            chkLivePreview.checked = dados.habilitarLivePreview;
            livePreviewAtivo = dados.habilitarLivePreview;
        }

        // Atualiza checkbox de mosaico vertical
        const chkMosaicoVertical = document.getElementById('chk-mosaico-vertical');
        if (chkMosaicoVertical) chkMosaicoVertical.checked = dados.mosaicoVertical;

        // Atualiza o slider de padding do mosaico
        const rangePadding = document.getElementById('range-padding-mosaico');
        if (rangePadding && dados.paddingMosaico !== undefined) {
            rangePadding.value = dados.paddingMosaico;
            atualizarPaddingVisual(dados.paddingMosaico);
        }

        // Atualiza inputs de resoluções de canvas
        const inputW = document.getElementById('input-canvas-w');
        const inputH = document.getElementById('input-canvas-h');
        if (inputW && dados.canvasLarguraHorizontal !== undefined) inputW.value = dados.canvasLarguraHorizontal;
        if (inputH && dados.canvasAlturaHorizontal !== undefined) inputH.value = dados.canvasAlturaHorizontal;

        const inputWV = document.getElementById('input-canvas-wv');
        const inputHV = document.getElementById('input-canvas-hv');
        if (inputWV && dados.canvasLarguraVertical !== undefined) inputWV.value = dados.canvasLarguraVertical;
        if (inputHV && dados.canvasAlturaVertical !== undefined) inputHV.value = dados.canvasAlturaVertical;

        // Atualiza botão do layout no header
        const btnLayout = document.getElementById('btn-layout-toggle');
        if (btnLayout) {
            if (dados.mosaicoVertical) {
                btnLayout.classList.add('active');
                btnLayout.innerHTML = '📐 Mosaico Vertical';
            } else {
                btnLayout.classList.remove('active');
                btnLayout.innerHTML = '📐 Mosaico Padrão';
            }
        }
    });
}

function definirFormatoAudio(formato) {
    fetch('/api/configuracoes/definir_audio/' + formato, { method: 'POST' })
    .then(res => {
        if (!res.ok) return res.json().then(e => alert(e.message));
        document.querySelectorAll('.btn-format').forEach(btn => btn.classList.remove('active'));
        const btn = document.getElementById('btn-audio-' + formato);
        if (btn) btn.classList.add('active');
    });
}

function definirQualidadeGravacao(qualidade) {
    fetch('/api/configuracoes/definir_qualidade/' + qualidade, { method: 'POST' })
    .then(res => {
        if (!res.ok) return res.json().then(e => alert(e.message));
        document.querySelectorAll('.btn-quality').forEach(btn => btn.classList.remove('active'));
        const btn = document.getElementById('btn-quality-' + qualidade);
        if (btn) btn.classList.add('active');
    });
}

function definirApagarTemporarios(valor) {
    fetch('/api/configuracoes/definir_temporarios/' + valor, { method: 'POST' })
    .then(res => {
        if (!res.ok) return res.json().then(e => alert(e.message));
    });
}

function definirHabilitarLogsDiagnostico(valor) {
    fetch('/api/configuracoes/definir_diagnostico/' + valor, { method: 'POST' })
    .then(res => {
        if (!res.ok) return res.json().then(e => alert(e.message));
    });
}

function definirHabilitarLivePreview(valor) {
    livePreviewAtivo = valor;
    fetch('/api/configuracoes/definir_live_preview/' + valor, { method: 'POST' })
    .then(res => {
        if (!res.ok) return res.json().then(e => alert(e.message));
    });
}

function definirMosaicoVertical(valor) {
    fetch('/api/configuracoes/definir_mosaico_vertical/' + valor, { method: 'POST' })
    .then(res => {
        if (!res.ok) return res.json().then(e => alert(e.message));
        carregarConfiguracoes();
    });
}

function atualizarPaddingVisual(valor) {
    const valSpan = document.getElementById('padding-value');
    if (valSpan) valSpan.textContent = valor + 'px';
}

function definirPaddingMosaico(valor) {
    fetch('/api/configuracoes/definir_padding/' + valor, { method: 'POST' })
    .then(res => {
        if (!res.ok) return res.json().then(e => alert(e.message));
    });
}

function alternarLayoutMosaico() {
    const btnLayout = document.getElementById('btn-layout-toggle');
    const novoValor = btnLayout ? !btnLayout.classList.contains('active') : false;
    
    if (btnLayout) {
        if (novoValor) {
            btnLayout.classList.add('active');
            btnLayout.innerHTML = '📐 Mosaico Vertical';
        } else {
            btnLayout.classList.remove('active');
            btnLayout.innerHTML = '📐 Mosaico Padrão';
        }
    }
    const chkMosaicoVertical = document.getElementById('chk-mosaico-vertical');
    if (chkMosaicoVertical) chkMosaicoVertical.checked = novoValor;
    
    definirMosaicoVertical(novoValor);
}

function mudarFundo(cor) {
    fetch('/api/definir_fundo/' + cor, { method: 'POST' })
    .then(res => {
        if (!res.ok) return res.json().then(e => alert(e.message));
        document.querySelectorAll('.btn-color-modal').forEach(b => b.classList.remove('active'));
        const btn = document.querySelector(`.bg-buttons-modal .btn-${cor}`);
        if (btn) btn.classList.add('active');
    });
}

function atualizarVolumeVisual(nome, valor, cardId) {
    const valSpan = document.getElementById(`vol-value-${cardId}`);
    if (valSpan) valSpan.textContent = valor + '%';
    
    const muteBtn = document.getElementById(`btn-mute-${cardId}`);
    if (muteBtn) {
        muteBtn.textContent = parseInt(valor) === 0 ? '🔇' : '🔊';
    }

    // Atualiza classe muted no container do VU
    const mask = document.getElementById(`vu-mask-${cardId}`);
    if (mask && mask.parentElement) {
        if (parseInt(valor) === 0) {
            mask.parentElement.classList.add('muted');
        } else {
            mask.parentElement.classList.remove('muted');
        }
    }
}

function restaurarVolumePadrao(nome, cardId) {
    const slider = document.getElementById(`slider-${cardId}`);
    if (slider) {
        slider.value = 100;
        alterarVolume(nome, 100, cardId);
    }
}

function alterarVolume(nome, valor, cardId) {
    atualizarVolumeVisual(nome, valor, cardId);
    fetch(`/api/audio/volume/${encodeURIComponent(nome)}/${valor}`, { method: 'POST' })
    .catch(err => console.error("Erro ao alterar volume:", err));
}

function toggleMute(nome, cardId) {
    const slider = document.getElementById(`slider-${cardId}`);
    if (!slider) return;
    
    const valorAtual = parseInt(slider.value);
    let novoValor = 0;
    
    if (valorAtual > 0) {
        ultimosVolumes[nome] = valorAtual;
        novoValor = 0;
    } else {
        novoValor = ultimosVolumes[nome] || 100;
    }
    
    slider.value = novoValor;
    alterarVolume(nome, novoValor, cardId);
}

function toggleGravar(nome, estaGravando) {
    const endpoint = estaGravando 
        ? `/api/gravar/parar?nome=${encodeURIComponent(nome)}` 
        : `/api/gravar/iniciar?nome=${encodeURIComponent(nome)}`;
    
    fetch(endpoint, { method: 'POST' })
    .then(res => {
        if (!res.ok) {
            return res.json()
                .then(e => alert(e.message || `Erro ao configurar a gravacao (Codigo ${res.status}).`))
                .catch(() => alert(`Erro no servidor: Retornou status HTTP ${res.status}. Verifique se o backend esta atualizado.`));
        }
        return res.json().then(d => {
            if (d.arquivo) {
                console.log("Arquivo de gravacao definido:", d.arquivo);
            }
            carregarFontes();
        });
    })
    .catch(err => {
        console.error("Erro na requisicao de gravar:", err);
        alert("Erro ao tentar se conectar ao backend para gravar.");
    });
}

function refreshPreview(btn, nome) {
    const wrapper = btn.closest('.preview-wrapper');
    const img = wrapper.querySelector('.feed-preview');
    img.classList.remove('oculto');
    btn.textContent = '⏳';
    img.onload  = () => { btn.textContent = '🔄'; };
    img.onerror = () => { btn.textContent = '🔄'; img.classList.add('oculto'); };
    img.src = '/api/preview/' + encodeURIComponent(nome) + '?t=' + Date.now();
}

function toggleSolo(nome) {
    const cards = document.querySelectorAll('.feed-card');
    cards.forEach(card => {
        if (card.querySelector('.feed-title').textContent === nome) {
            const isSolo = card.classList.contains('solo');
            cards.forEach(c => {
                c.classList.remove('solo');
                const s = c.querySelector('.btn-solo span');
                if (s) s.textContent = isDock ? '🎯 Solo' : '🎯 Solo';
            });
            if (!isSolo) {
                card.classList.add('solo');
                card.classList.remove('highlighted');
                const sh = card.querySelector('.btn-highlight span');
                if (sh) sh.textContent = isDock ? '⭐ Destaque' : '⭐ Destaque';
                const s = card.querySelector('.btn-solo span');
                if (s) s.textContent = isDock ? '🎯 Tirar' : '🎯 Solo (Remover)';
            }
        }
    });
    fetch('/api/solo/' + encodeURIComponent(nome), { method: 'POST' })
    .then(res => { if (!res.ok) return res.json().then(e => { alert(e.message); carregarFontes(); }); })
    .catch(() => carregarFontes());
}

function toggleHighlight(nome) {
    const cards = document.querySelectorAll('.feed-card');
    cards.forEach(card => {
        if (card.querySelector('.feed-title').textContent === nome) {
            const hl = card.classList.contains('highlighted');
            if (!hl) {
                cards.forEach(c => {
                    c.classList.remove('highlighted');
                    const s = c.querySelector('.btn-highlight span');
                    if (s) s.textContent = isDock ? '⭐ Destaque' : '⭐ Destaque';
                });
                card.classList.add('highlighted');
                const s = card.querySelector('.btn-highlight span');
                if (s) s.textContent = isDock ? '⭐ Remover' : '⭐ Em Destaque (Remover)';
            } else {
                card.classList.remove('highlighted');
                const s = card.querySelector('.btn-highlight span');
                if (s) s.textContent = isDock ? '⭐ Destaque' : '⭐ Destaque';
            }
        }
    });
    fetch('/api/highlight/' + encodeURIComponent(nome), { method: 'POST' })
    .then(res => { if (!res.ok) return res.json().then(e => { alert(e.message); carregarFontes(); }); })
    .catch(() => carregarFontes());
}

function mudarPosicao(nome, novaPos) {
    document.querySelectorAll('.feed-card').forEach(card => {
        if (card.querySelector('.feed-title').textContent === nome) {
            card.querySelectorAll('.btn-pos').forEach((b, i) => b.classList.toggle('active', i === novaPos));
            const badge = card.querySelector('.order-badge');
            if (badge) badge.textContent = novaPos + 1;
        }
    });
    fetch('/api/posicao/' + encodeURIComponent(nome) + '/' + novaPos, { method: 'POST' })
    .then(res => { if (!res.ok) return res.json().then(e => { alert(e.message); carregarFontes(); }); })
    .catch(() => carregarFontes());
}

function salvarApelido(btn, nome, inputId) {
    const input = document.getElementById(inputId);
    const apelido = input.value;
    btn.disabled = true;
    const orig = btn.textContent;
    btn.textContent = '✓';
    
    fetch('/api/definir_apelido/' + encodeURIComponent(nome), {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ apelido })
    })
    .then(r => r.json())
    .then(d => {
        setTimeout(() => { btn.textContent = orig; btn.disabled = false; }, 1200);
        if (d.status !== 'ok') alert(d.message);
    })
    .catch(() => { btn.textContent = orig; btn.disabled = false; carregarFontes(); });
}

function autoSalvarApelido(input, nome) {
    const apelido = input.value;
    fetch('/api/definir_apelido/' + encodeURIComponent(nome), {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ apelido })
    });
}

function conectarSSE() {
    const sseDot = isDock ? document.getElementById('status-conexao') : document.getElementById('sse-dot');
    const sseLabel = isDock ? document.getElementById('texto-conexao') : document.getElementById('sse-label');
    const metricsContainer = document.getElementById('metrics-container');

    const src = new EventSource('/api/eventos');
    
    src.onopen = () => { 
        if (isDock) {
            sseDot.className = 'sse-dot conectado'; 
            sseLabel.textContent = 'Conectado';
        } else {
            sseDot.className = 'sse-dot conectado'; 
            sseLabel.textContent = 'Tempo real • Atualização instantânea'; 
        }
        if (metricsContainer) {
            metricsContainer.style.display = 'flex';
        }
    };
    
    src.onmessage = () => { carregarFontes(); carregarConfiguracoes(); };
    
    src.addEventListener('vu', (e) => {
        try {
            const dadosVu = JSON.parse(e.data);
            for (const [nome, valor] of Object.entries(dadosVu)) {
                const cardId = 'card-' + nome.replace(/[^a-zA-Z0-9]/g, '_');
                const mask = document.getElementById(`vu-mask-${cardId}`);
                if (mask) {
                    mask.style.width = (100 - valor) + '%';
                }
            }
            document.querySelectorAll('.vu-meter-mask').forEach(mask => {
                const id = mask.id.replace('vu-mask-', '');
                let encontrado = false;
                for (const nome of Object.keys(dadosVu)) {
                    const cardIdPayload = 'card-' + nome.replace(/[^a-zA-Z0-9]/g, '_');
                    if (cardIdPayload === id) {
                        encontrado = true;
                        break;
                    }
                }
                if (!encontrado) {
                    mask.style.width = '100%';
                }
            });
        } catch(err) {
            console.error("Erro ao processar dados de VU:", err);
        }
    });

    src.addEventListener('metrics', (e) => {
        try {
            const metrics = JSON.parse(e.data);
            const cpuVal = document.getElementById('metric-cpu');
            const ramVal = document.getElementById('metric-ram');
            const fpsMosaicoVal = document.getElementById('metric-fps-mosaico');
            const fpsVerticalVal = document.getElementById('metric-fps-vertical');

            if (cpuVal) cpuVal.textContent = metrics.cpu.toFixed(1) + '%';
            if (ramVal) ramVal.textContent = metrics.ram.toFixed(0) + ' MB';
            if (fpsMosaicoVal) fpsMosaicoVal.textContent = metrics.fpsMosaico.toFixed(1);
            if (fpsVerticalVal) fpsVerticalVal.textContent = metrics.fpsVertical.toFixed(1);
            // Atualiza status de rede para feeds ativos no payload
            const fontesAtualizadas = new Set();
            if (metrics.fontes && Array.isArray(metrics.fontes)) {
                metrics.fontes.forEach(f => {
                    const cardId = 'card-' + f.nome.replace(/[^a-zA-Z0-9]/g, '_');
                    fontesAtualizadas.add(cardId);
                    const card = document.getElementById(cardId);
                    if (card) {
                        const netIcon = card.querySelector('.net-status-icon');
                        if (netIcon) {
                            netIcon.style.display = 'inline-block';
                            
                            const vFrames = f.v_frames || 0;
                            const vDrop = f.v_drop || 0;
                            const aFrames = f.a_frames || 0;
                            const aDrop = f.a_drop || 0;
                            
                            // Calcula porcentagem de perda de frames de vídeo
                            const totalVideo = vFrames + vDrop;
                            const lossRate = totalVideo > 0 ? (vDrop / totalVideo) * 100 : 0;
                            
                            // Define cores com base no nível de perda
                            netIcon.className = 'net-status-icon';
                            if (lossRate > 2.0) {
                                netIcon.classList.add('danger');
                            } else if (lossRate > 0.2 || vDrop > 0) {
                                netIcon.classList.add('warning');
                            } else {
                                netIcon.classList.add('good');
                            }
                            
                            // Monta o tooltip descritivo em português brasileiro
                            const fpsStr = f.fps ? f.fps.toFixed(2) + ' FPS' : 'Desconhecido';
                            netIcon.title = `Saúde da Conexão NDI:\n` +
                                            `• FPS de Entrada: ${fpsStr}\n` +
                                            `• Vídeo Recebido: ${vFrames.toLocaleString()} frames\n` +
                                            `• Vídeo Perdido (Drops): ${vDrop.toLocaleString()} (${lossRate.toFixed(2)}%)\n` +
                                            `• Áudio Recebido: ${aFrames.toLocaleString()} frames\n` +
                                            `• Áudio Perdido: ${aDrop.toLocaleString()}`;
                        }
                    }
                });
            }

            // Oculta apenas os ícones de fontes que não estão mais ativas
            document.querySelectorAll('.net-status-icon').forEach(icon => {
                const card = icon.closest('.feed-card');
                if (card && !fontesAtualizadas.has(card.id)) {
                    icon.style.display = 'none';
                }
            });
        } catch (err) {
            console.error("Erro ao ler métricas de performance:", err);
        }
    });
    
    src.onerror = () => {
        if (isDock) {
            sseDot.className = 'sse-dot erro';
            sseLabel.textContent = 'Reconectando...';
        } else {
            sseDot.className = 'sse-dot erro';
            sseLabel.textContent = 'Falha SSE • Usando polling';
        }
        if (metricsContainer) {
            metricsContainer.style.display = 'none';
        }
        src.close();
        setTimeout(conectarSSE, 5000);
    };
}

window.onload = function() {
    conectarSSE();

    // Intervalo para atualizar cronômetros de gravação em tempo real (1s)
    setInterval(() => {
        document.querySelectorAll('.recording-timer-badge').forEach(badge => {
            const desde = badge.getAttribute('data-desde');
            if (desde) {
                const epochDesde = parseFloat(desde);
                if (!isNaN(epochDesde) && epochDesde > 0) {
                    const decorrido = Math.floor(Date.now() / 1000 - epochDesde);
                    if (decorrido >= 0) {
                        badge.textContent = formatarTempo(decorrido);
                    } else {
                        badge.textContent = "00:00";
                    }
                }
            }
        });
    }, 1000);

    // Atualização automática periódica dos previews (a cada 3 segundos)
    setInterval(atualizarPreviewsAutomatico, 3000);

    setInterval(carregarFontes, 5000);
    carregarFontes();
    carregarConfiguracoes();
};

function atualizarPreviewsAutomatico() {
    if (!livePreviewAtivo) return;
    document.querySelectorAll('.feed-card').forEach(card => {
        const img = card.querySelector('.feed-preview');
        if (img && !img.classList.contains('oculto')) {
            const titleEl = card.querySelector('.feed-title');
            if (titleEl && !card.classList.contains('erro')) {
                const nomeFonte = titleEl.textContent;
                img.src = '/api/preview/' + encodeURIComponent(nomeFonte) + '?t=' + Date.now();
            }
        }
    });
}

function formatarTempo(segundos) {
    const h = Math.floor(segundos / 3600);
    const m = Math.floor((segundos % 3600) / 60);
    const s = segundos % 60;
    const pad = (n) => String(n).padStart(2, '0');
    if (h > 0) {
        return `${pad(h)}:${pad(m)}:${pad(s)}`;
    } else {
        return `${pad(m)}:${pad(s)}`;
    }
}

function definirResolucaoHorizontal(w, h) {
    fetch(`/api/configuracoes/definir_resolucao_horizontal/${w}/${h}`, { method: 'POST' })
    .then(res => {
        if (!res.ok) return res.json().then(e => alert(e.message));
        console.log(`Resolução horizontal atualizada para ${w}x${h}`);
    })
    .catch(err => console.error("Erro ao definir resolução horizontal:", err));
}

function definirResolucaoVertical(w, h) {
    fetch(`/api/configuracoes/definir_resolucao_vertical/${w}/${h}`, { method: 'POST' })
    .then(res => {
        if (!res.ok) return res.json().then(e => alert(e.message));
        console.log(`Resolução vertical atualizada para ${w}x${h}`);
    })
    .catch(err => console.error("Erro ao definir resolução vertical:", err));
}

function aplicarResolucaoHorizontal() {
    const w = parseInt(document.getElementById('input-canvas-w').value);
    const h = parseInt(document.getElementById('input-canvas-h').value);
    if (isNaN(w) || isNaN(h)) {
        alert("Largura e Altura devem ser números válidos.");
        return;
    }
    definirResolucaoHorizontal(w, h);
}

function aplicarResolucaoVertical() {
    const w = parseInt(document.getElementById('input-canvas-wv').value);
    const h = parseInt(document.getElementById('input-canvas-hv').value);
    if (isNaN(w) || isNaN(h)) {
        alert("Largura e Altura devem ser números válidos.");
        return;
    }
    definirResolucaoVertical(w, h);
}
