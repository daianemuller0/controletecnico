// Utilitários chamados pelo Blazor via JS interop.
window.app = window.app || {};

app.apresentacao = (on) => {
    document.body.classList.toggle('apresentacao', !!on);
    try { localStorage.setItem('ct-apres', on ? '1' : '0'); } catch { }
    setTimeout(() => { for (const id in app.mapa._m) app.mapa._m[id].map.invalidateSize(); }, 250);
};

app.apresentacaoAtual = () => document.body.classList.contains('apresentacao');

app.baixarTexto = (nome, mime, texto, bom) => {
    const blob = new Blob([bom ? '﻿' : '', texto], { type: mime });
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob); a.download = nome;
    document.body.appendChild(a); a.click(); a.remove();
    setTimeout(() => URL.revokeObjectURL(a.href), 2000);
};

app.imprimir = () => window.print();

// ---------------------------------------------------------------- mapa (Leaflet)
app.mapa = {
    _m: {},

    iniciar(id, ref, cfg) {
        const el = document.getElementById(id);
        if (!el || typeof L === 'undefined') return false;
        this.destruir(id);
        const map = L.map(el, { zoomControl: true, worldCopyJump: true }).setView(cfg.centro || [-15, -52], cfg.zoom || 4);
        if (cfg.tileUrl) {
            L.tileLayer(cfg.tileUrl, { maxZoom: 18, attribution: cfg.atribuicao || '' }).addTo(map);
        }
        const camada = L.layerGroup().addTo(map);
        this._m[id] = { map, camada, ref, ro: null };
        // o contêiner muda de tamanho (painel lateral, modo apresentação): reajusta
        if (window.ResizeObserver) {
            const ro = new ResizeObserver(() => map.invalidateSize());
            ro.observe(el); this._m[id].ro = ro;
        }
        setTimeout(() => map.invalidateSize(), 50);
        return true;
    },

    destruir(id) {
        const o = this._m[id];
        if (!o) return;
        try { if (o.ro) o.ro.disconnect(); o.map.remove(); } catch { }
        delete this._m[id];
    },

    // dados: { grupos:[{chave,lat,lon,titulo,cor,tracejado,rotulo,tecnicos:[{nome,cor}]}], rotas:[{de,para,cor,titulo}], ajustar:bool }
    desenhar(id, dados) {
        const o = this._m[id];
        if (!o) return;
        o.camada.clearLayers();
        const pts = [];
        for (const r of dados.rotas || []) {
            const l = L.polyline([r.de, r.para], { color: r.cor, weight: 3, dashArray: '8 8', opacity: .85 }).addTo(o.camada);
            l.bindTooltip(r.titulo, { sticky: true });
            L.circleMarker(r.de, { radius: 5, color: r.cor, fillColor: '#fff', fillOpacity: 1, weight: 2 }).addTo(o.camada);
            pts.push(r.de, r.para);
        }
        for (const g of dados.grupos || []) {
            const n = g.tecnicos.length;
            const html = `<div class="mk ${g.tracejado ? 'mk-prev' : 'mk-conf'} ${g.viagem ? 'mk-viagem' : ''}" style="--c:${g.cor}">` +
                `<span class="mk-n">${n > 1 ? n : g.iniciais}</span></div>`;
            const icon = L.divIcon({ html, className: 'mk-wrap', iconSize: [38, 38], iconAnchor: [19, 19] });
            const m = L.marker([g.lat, g.lon], { icon, title: g.titulo, keyboard: true, riseOnHover: true }).addTo(o.camada);
            const nomes = g.tecnicos.map(t => `<div class="mk-tip-l"><span class="mk-dot" style="background:${t.cor}"></span>${t.nome}</div>`).join('');
            m.bindTooltip(`<div class="mk-tip"><strong>${g.titulo}</strong><div class="mk-tip-s">${g.rotulo}</div>${nomes}</div>`, { direction: 'top', offset: [0, -16] });
            m.on('click', () => o.ref && o.ref.invokeMethodAsync('AoClicarGrupo', g.chave));
            pts.push([g.lat, g.lon]);
        }
        if (dados.ajustar && pts.length) {
            if (pts.length === 1) o.map.setView(pts[0], 9);
            else o.map.fitBounds(L.latLngBounds(pts).pad(0.25), { maxZoom: 11 });
        }
    },

    foco(id, lat, lon, zoom) { const o = this._m[id]; if (o) o.map.setView([lat, lon], zoom || 10); },

    // seletor de posição (cadastro de planta): clique ou arraste o marcador
    escolher(id, ref, lat, lon, cfg) {
        if (!this.iniciar(id, ref, { centro: lat != null ? [lat, lon] : (cfg.centro || [-15, -52]), zoom: lat != null ? 14 : 4, tileUrl: cfg.tileUrl, atribuicao: cfg.atribuicao })) return false;
        const o = this._m[id];
        let mk = null;
        const colocar = (la, lo, avisar) => {
            if (mk) mk.setLatLng([la, lo]);
            else {
                mk = L.marker([la, lo], { draggable: true }).addTo(o.map);
                mk.on('dragend', () => { const p = mk.getLatLng(); ref.invokeMethodAsync('AoMover', p.lat, p.lng); });
            }
            if (avisar) ref.invokeMethodAsync('AoMover', la, lo);
        };
        if (lat != null) colocar(lat, lon, false);
        o.map.on('click', e => colocar(e.latlng.lat, e.latlng.lng, true));
        o.pos = (la, lo) => { colocar(la, lo, false); o.map.setView([la, lo], 14); };
        return true;
    },
    definir(id, lat, lon) { const o = this._m[id]; if (o && o.pos) o.pos(lat, lon); },
};

// ------------------------------------------------- arrastar e soltar (agenda)
app.dnd = {
    // Elementos [data-dnd] podem ser arrastados para células [data-drop]. Ao soltar chama
    // ref.AoSoltar(origem, destino, diasPegos) — o servidor valida ANTES de salvar.
    iniciar(raiz, ref) {
        const el = document.getElementById(raiz);
        if (!el || el._dnd) return;
        el._dnd = true;
        el.addEventListener('dragstart', e => {
            const d = e.target.closest('[data-dnd]');
            if (!d) return;
            e.dataTransfer.setData('text/plain', d.dataset.dnd);
            e.dataTransfer.effectAllowed = 'move';
            const cel = el.querySelector('[data-drop]');
            const w = cel ? cel.getBoundingClientRect().width : 0;
            el._grab = (w > 0 && d.dataset.dndgrab !== 'off') ? Math.max(0, Math.floor(e.offsetX / w)) : 0;
            setTimeout(() => { d.classList.add('arrastando'); el.classList.add('dnd-on'); }, 0);
        });
        el.addEventListener('dragend', () => {
            el.classList.remove('dnd-on');
            el.querySelectorAll('.arrastando,.drop-ativo').forEach(x => x.classList.remove('arrastando', 'drop-ativo'));
        });
        el.addEventListener('dragover', e => {
            const t = e.target.closest('[data-drop]');
            if (!t) return;
            e.preventDefault();
            e.dataTransfer.dropEffect = 'move';
            el.querySelectorAll('.drop-ativo').forEach(x => x !== t && x.classList.remove('drop-ativo'));
            t.classList.add('drop-ativo');
        });
        el.addEventListener('drop', e => {
            const t = e.target.closest('[data-drop]');
            if (!t) return;
            e.preventDefault();
            const origem = e.dataTransfer.getData('text/plain');
            el.classList.remove('dnd-on');
            ref.invokeMethodAsync('AoSoltar', origem, t.dataset.drop, el._grab || 0);
        });
    },
    parar(raiz) { const el = document.getElementById(raiz); if (el) el._dnd = false; }
};

// restaura o modo apresentação (preferência da pessoa neste navegador)
try { if (localStorage.getItem('ct-apres') === '1') document.body.classList.add('apresentacao'); } catch { }
