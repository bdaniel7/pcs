const API = {
  async json(path) {
    const res = await fetch(path, { headers: { Accept: 'application/json' } });
    if (!res.ok) throw new Error(`${path} -> ${res.status}`);
    return (await res.json()).MediaContainer;
  }
};

const state = { library: null, item: null };

const el = (sel) => document.querySelector(sel);

function ms(value) {
  if (!value) return '';
  const total = Math.round(value / 1000);
  const h = Math.floor(total / 3600);
  const m = Math.round((total % 3600) / 60);
  return h > 0 ? `${h}h ${m}m` : `${m}m`;
}

function banner(message, isError) {
  const node = el('#banner');
  node.textContent = message;
  node.className = message ? (isError ? 'error' : 'loading') : '';
}

function renderLibraries(libraries) {
  el('#back').hidden = true;
  el('#player').hidden = true;
  el('#grid').hidden = false;
  el('#detail').hidden = true;
  el('#heading').textContent = 'Libraries';

  const cards = libraries.Directory.map((d) => `
    <button class="card library" data-key="${d.ratingKey}">
      <div class="art library-art">${d.type === 'movie' ? '\u{1F3AC}' : '\u{1F4FA}'}</div>
      <div class="meta">
        <div class="title">${d.title}</div>
        <div class="sub">${d.type}</div>
      </div>
    </button>`);

  el('#grid').innerHTML = cards.join('') || '<p class="empty">No libraries found.</p>';
}

async function openLibrary(key) {
  banner('Loading\u2026');
  const [section, all] = await Promise.all([
    API.json('/library/sections'),
    API.json(`/library/sections/${key}/all`)
  ]);

  const lib = section.Directory.find((d) => d.ratingKey === String(key));
  const items = all.Metadata || [];

  state.library = key;
  el('#back').hidden = false;
  el('#grid').hidden = false;
  el('#detail').hidden = true;
  el('#player').hidden = true;
  el('#heading').textContent = lib ? lib.title : 'Library';
  banner('');

  const cards = items.map((m) => {
    const year = m.year ? `<span>${m.year}</span>` : '';
    const dur = ms(m.duration) ? `<span>${ms(m.duration)}</span>` : '';
    return `
      <button class="card" data-key="${m.ratingKey}">
        <img class="art" loading="lazy" src="${m.thumb}" alt="">
        <div class="meta">
          <div class="title">${m.title}</div>
          <div class="sub">${[year, dur].filter(Boolean).join(' \u00b7 ')}</div>
        </div>
      </button>`;
  });

  el('#grid').innerHTML = cards.join('') || '<p class="empty">This library has no items.</p>';
}

async function openItem(key) {
  banner('Loading\u2026');
  const all = await Promise.all([API.json(`/library/metadata/${key}`)]);
  const item = all[0].Metadata[0];
  state.item = item;

  el('#grid').hidden = true;
  el('#detail').hidden = false;
  el('#player').hidden = true;
  banner('');

  const media = (item.Media || [])[0] || {};
  const part = (media.Part || [])[0] || {};

  el('#detail').innerHTML = `
    <img class="poster" src="${item.art || item.thumb}" alt="">
    <div class="info">
      <h1>${item.title}</h1>
      <p class="facts">
        ${[item.year, ms(item.duration), media.width ? `${media.width}x${media.height}` : null,
           media.videoCodec, media.audioCodec]
          .filter(Boolean).join(' \u00b7 ')}
      </p>
      <p class="summary">${item.summary || 'No summary available.'}</p>
      <p class="streams">
        ${(part.Stream || []).map((s) =>
          `<span class="chip">${s.streamType === 1 ? 'Video' : s.streamType === 2 ? 'Audio' : 'Subtitle'}: ${s.codec}</span>`
        ).join('')}
      </p>
      <div class="actions">
        <button class="play" data-part="${part.key || ''}">Play</button>
        <button class="ghost" onclick="history.back()">Back</button>
      </div>
    </div>`;

  el('#detail').querySelector('.play').onclick = () => play(part.key, item.title);
}

function play(partKey, title) {
  if (!partKey) {
    banner('This item has no playable part.', true);
    return;
  }

  const video = el('#video');
  video.poster = '';
  video.src = partKey;
  el('#player').hidden = false;
  el('#detail').hidden = true;
  el('#grid').hidden = true;
  el('#heading').textContent = title;
  el('#back').hidden = false;
  video.play().catch(() => banner('Press play on the video controls.', true));
}

function showHome() {
  state.library = null;
  state.item = null;
  el('#player').hidden = true;
  loadLibraries();
}

async function loadLibraries() {
  banner('Loading\u2026');
  try {
    renderLibraries(await API.json('/library/sections'));
    banner('');
  } catch (err) {
    banner(`Failed to load libraries: ${err.message}`, true);
  }
}

document.addEventListener('click', (event) => {
  const card = event.target.closest('.card[data-key]');
  if (card) {
    const key = card.dataset.key;
    if (event.target.closest('.card.library')) openLibrary(key).catch(fail);
    else openItem(key).catch(fail);
  }
});

el('#back').onclick = () => {
  if (!el('#player').hidden) {
    el('#video').pause();
    el('#player').hidden = true;
    if (state.item) openItem(state.item.ratingKey).catch(fail);
    else if (state.library) openLibrary(state.library).catch(fail);
    else showHome();
  } else if (state.library) {
    openLibrary(state.library).catch(fail);
  } else {
    showHome();
  }
};

function fail(err) {
  console.error(err);
  banner(`Request failed: ${err.message}`, true);
}

loadLibraries();