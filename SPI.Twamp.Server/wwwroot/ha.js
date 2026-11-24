(() => {
  const by = id => document.getElementById('ha-' + id);
  let base, draft, preview, dirty = false;
  const message = text => { by('notice').textContent = text; };
  const col = (tr, ...values) => {
    const td = tr.insertCell();
    for (const value of values) td.append(typeof value === 'string' ? document.createTextNode(value) : value);
    return td;
  };
  function button(text, action) {
    const b = document.createElement('button');
    b.type = 'button'; b.textContent = text; b.onclick = () => guarded(action);
    return b;
  }
  async function guarded(fn) {
    try { await fn(); } catch (e) { message(e.message); }
  }
  async function request(path = '', body) {
    const options = {headers: hdrs(), cache: 'no-store'};
    if (body !== undefined) {
      options.method = 'POST'; options.headers['Content-Type'] = 'application/json'; options.body = JSON.stringify(body);
    }
    const res = await fetch('/api/ha' + path, options);
    if (!res.ok) throw new Error(await res.text() || ('HTTP ' + res.status));
    return res.json();
  }
  function select(items, value, label, changed) {
    const s = document.createElement('select'); s.setAttribute('aria-label', label);
    for (const [id,name] of items) s.add(new Option(name,id));
    s.value = value || ''; if (changed) s.onchange = () => changed(s.value);
    return s;
  }
  function invalidate() {
    dirty = true; preview = null; by('plan').replaceChildren(); by('apply').disabled = true;
    message('Изменения в черновике. Действует прежний план.');
  }
  async function open(force = false) {
    if (base && !force) return;
    const data = await request();
    base = data;
    draft = {version:data.version, collectors:data.plan.collectors.map(c=>({...c})), probes:data.plan.assignments.map(a=>({...a.probe})), reassign:[]};
    dirty = false; preview = null;
    by('known').replaceChildren(...[...data.existing,...data.discovered].map(p=>new Option(p.name,p.url)));
    by('replacement').replaceChildren(); by('plan').replaceChildren(); by('apply').disabled = true;
    by('revision').textContent = 'Действующий план № ' + data.version;
    message(''); render();
  }
  const items = nodes => nodes.map(c=>[c.id,c.id+' · '+c.machine]);
  const node = id => draft.collectors.find(c=>c.id===id);
  function alternatives(peer, removed) {
    const machine = node(peer)?.machine.toLowerCase();
    return draft.collectors.filter(c=>c.id!==removed && c.machine.toLowerCase()!==machine);
  }
  function removeCollector(id) {
    const panel = by('replacement'); panel.replaceChildren();
    const affected = draft.probes.filter(p=>[p.first,p.second].includes(id));
    const picks = affected.map(p=>{
      const peer = p.first===id ? p.second : p.first;
      const s = select([['','Выберите замену'],...items(alternatives(peer,id))],'','Замена для '+p.name);
      const label = document.createElement('label'); label.append(p.name+' ',s); panel.append(label,' ');
      return {p,s};
    });
    panel.append(button('Удалить '+id+' из черновика',()=>{
      if (picks.some(({s})=>!s.value)) throw new Error('Для каждой связанной пробы нужен другой съёмник на отдельной машине.');
      for (const {p,s} of picks) {
        if (p.first===id) p.first=s.value; else p.second=s.value;
        if (p.pin===id) p.pin=s.value;
      }
      draft.collectors=draft.collectors.filter(c=>c.id!==id); panel.replaceChildren(); invalidate();render();
    }),button('Отмена',()=>panel.replaceChildren()));
  }
  function render() {
    const nodes=by('nodes'), probes=by('probes'); nodes.replaceChildren();probes.replaceChildren();by('replacement').replaceChildren();
    for (const c of draft.collectors) {
      const tr=nodes.insertRow(); col(tr,c.id+' · '+c.machine); col(tr,c.url);
      col(tr,'').dataset.collectorHealth=c.id;
      const cap=document.createElement('input'); cap.type='number';cap.min='0.01';cap.step='any';cap.value=c.capacity;cap.setAttribute('aria-label','Производительность '+c.id);
      cap.onchange=()=>{c.capacity=Number(cap.value);invalidate();};col(tr,cap);
      col(tr,select([['work','Балансировка'],['reserve','Горячий резерв']],c.standby?'reserve':'work','Режим '+c.id,v=>{c.standby=v==='reserve';invalidate();}));
      col(tr,button('Удалить',()=>removeCollector(c.id)));
    }
    for (const p of draft.probes) {
      const tr=probes.insertRow(); col(tr,p.name,document.createElement('br'),p.url);
      for (const [key,other] of [['first','second'],['second','first']]) {
        // Clear a slot to exchange the order or replace both members of a pair.
        const options=[['','Выберите съёмник'],...items(alternatives(p[other]))];
        col(tr,select(options,p[key],key==='first'?'Съёмник 1':'Съёмник 2',v=>{
          p[key]=v;if (p.pin && ![p.first,p.second].includes(p.pin)) p.pin=null;invalidate();render();
        }));
      }
      col(tr,select([['','Автоматически'],...items(draft.collectors.filter(c=>[p.first,p.second].includes(c.id)))],p.pin,'Основной для '+p.name,v=>{p.pin=v||null;invalidate();}));
      col(tr,base.states[p.url]?.owner || '—').dataset.collectorOwner=p.url;
      col(tr,button('Проверить связи',async()=>{
        const results=await Promise.all([p.first,p.second].map(async id=>{
          if(!node(id))return 'не назначен';
          const q=new URLSearchParams({url:node(id).url,probe:p.url});
          const r=await request('/link?'+q);return id+' — '+(r.reachable?'доступна':'нет связи');
        }));message(p.name+' · '+results.join(', '));
      }),button('Переименовать',()=>{const title=prompt('Имя пробы',p.name);if(title?.trim()){p.name=title.trim();invalidate();render();}}),
      button('Удалить',()=>{draft.probes=draft.probes.filter(r=>r!==p);invalidate();render();}));
    }
    paintHealth();
  }
  function paintHealth(){
    by('nodes').querySelectorAll('[data-collector-health]').forEach(td=>{
      const id=td.dataset.collectorHealth, h=base.health?.[id], assigned=Object.values(base.states).filter(s=>s.owner===id);
      td.textContent=h ? (h.online?'На связи':'Нет связи')+' · проб '+assigned.length+' · работа/с '+Math.round(assigned.reduce((sum,s)=>sum+(s.workPerSecond||0),0))+' · запись '+fmt(h.lastUploadAt) : 'Нет наблюдений';
    });
    by('probes').querySelectorAll('[data-collector-owner]').forEach(td=>{td.textContent=base.states[td.dataset.collectorOwner]?.owner || '—';});
  }
  by('register').onsubmit=e=>{e.preventDefault();guarded(async()=>{
    const url=e.target.elements.url.value.trim(), id=await request('/identify?url='+encodeURIComponent(url));
    const prior=node(id.id);
    if(prior) Object.assign(prior,{url,machine:id.machine}); else draft.collectors.push({id:id.id,url,machine:id.machine,capacity:100,standby:false});
    e.target.reset();invalidate();render();
  });};
  by('add-probe').onsubmit=e=>{e.preventDefault();guarded(()=>{
    const name=e.target.elements.name.value.trim(),url=new URL(e.target.elements.url.value).href.replace(/\/$/,'');
    if(draft.probes.some(p=>p.url===url))throw new Error('Проба уже добавлена.');
    const a=draft.collectors[0],b=a && alternatives(a.id)[0];
    if(!b)throw new Error('Для регистрации нужны два съёмника на разных машинах.');
    draft.probes.push({name,url,first:a.id,second:b.id,pin:null});e.target.reset();invalidate();render();
  });};
  by('reload').onclick=()=>guarded(()=>{if(!dirty || confirm('Отбросить черновик и перечитать реестр?'))return open(true);});
  by('preview').onclick=()=>guarded(async()=>{
    const input=JSON.stringify(draft);
    const result=await request('/preview',draft);
    if(input!==JSON.stringify(draft))return;
    const table=document.createElement('table');const h=table.createTHead().insertRow();
    for(const text of ['Проба','Сейчас','Основной по плану','Резерв']){const th=document.createElement('th');th.textContent=text;h.append(th);}
    const totals={}, failures={}; const body=table.createTBody();
    for(const a of result.plan.assignments){
      const tr=body.insertRow(),p=a.probe,other=p.first===a.primary?p.second:p.first;
      const owner=base.states[p.url]?.owner; [p.name,owner||'—',a.primary,other].forEach(t=>col(tr,t));
      const w=result.weights[p.url]??1;totals[a.primary]=(totals[a.primary]??0)+w;
      const host=node(a.primary).machine.toLowerCase();
      (failures[other]??={})[host]=(failures[other][host]??0)+w;
    }
    const panel=by('plan');panel.replaceChildren(table);
    for(const c of draft.collectors){
      const usual=totals[c.id]||0, peak=usual+Math.max(0,...Object.values(failures[c.id]||{}));
      const line=document.createElement('p');line.textContent=c.id+' · по плану '+Math.round(100*usual/c.capacity)+'% мощности · при отказе одного узла до '+Math.round(100*peak/c.capacity)+'%';panel.append(line);
    }
    draft.reassign=result.plan.assignments.filter(a=>base.states[a.probe.url]?.owner && base.states[a.probe.url].owner!==a.primary).map(a=>a.probe.url);
    preview=Object.fromEntries(result.plan.assignments.map(a=>[a.probe.url,a.primary]));
    by('apply').disabled=false;message('Проверьте распределение перед применением.');
  });
  by('apply').onclick=()=>guarded(async()=>{
    if(!preview)return; by('apply').disabled=true;
    try{await request('/apply',{...draft,approved:preview});await open(true);message('План применён.');}
    finally{by('apply').disabled=!preview;}
  });
  window.twampHa={enabled:false,open:()=>guarded(()=>open()),refresh:()=>guarded(async()=>{
    if(!base)return;
    const live=await request('/state');Object.assign(base,live);paintHealth();
  })};
  async function mode(){
    const m=await request('/mode');window.twampHa.enabled=m.enabled;
    document.querySelector('[data-tab="ha"]').hidden=!m.enabled;
    if(m.enabled){document.getElementById('collectorUrl').closest('.card').hidden=true;document.getElementById('tblUnident').closest('.card').hidden=true;renderProbes();}
  }
  keyInput.addEventListener('change',()=>guarded(mode));
  guarded(mode);
})();
