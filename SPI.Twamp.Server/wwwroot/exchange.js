// Each sample is a read-only response from one node. A failed read has no counters.
let exchangeNodes = new Map();
let exchangeCollectors = [];
let exchangeRequest;

function exchangeOwner(probe) {
  const sample = exchangeNodes.get(probe.requestInfo);
  if (sample?.error) return null;
  return sample?.data?.ordered ? sample.data.owner : probe.owner;
}

function pressure(disk) {
  if (!disk || !disk.totalBytes) return '';
  if (disk.availableBytes === 0 || disk.freeInodes === 0) return 'Диск заполнен';
  return disk.availableBytes / disk.totalBytes <= .1 ? 'Мало места на диске' : '';
}

function exchangeStatus(probe) {
  const sample = exchangeNodes.get(probe.requestInfo);
  const data = sample?.data;
  const ownerId = data?.ordered ? data.owner : probe.owner;
  const owner = exchangeCollectors.find(c => c.id === ownerId);
  let label;
  if (!sample) label = 'Нет текущих данных';
  else if (!data) label = 'Проба недоступна';
  else if (pressure(data.disk)) label = pressure(data.disk);
  else if (data.ordered && !data.owner) label = 'Нет активного съёмника';
  else if (ownerId && !owner?.data) label = 'Нет статистики активного съёмника';
  else if (owner?.data?.transfers?.[probe.requestInfo]?.error || owner?.data?.storage?.online === false)
    label = 'Ошибка доставки';
  else if (probe.backoffSeconds) label = 'Ошибка доставки';
  else if (data.pendingResults > 0) label = 'Ждут подтверждения: ' + num(data.pendingResults);
  else if (data.lastAck && owner?.data) {
    return {good:true, html:'<span class="ok">Очередь пуста · есть подтверждение</span>'};
  } else label = 'Нет текущего подтверждения обмена';
  return {good:false, html:'<span class="bad">' + escHtml(label) + '</span>'};
}

function bytes(value) {
  if (value == null) return '—';
  if (value < 1048576) return num(value) + ' Б';
  const gib = value >= 1073741824;
  return (value / (gib ? 1073741824 : 1048576)).toLocaleString('ru-RU', {maximumFractionDigits:1}) + (gib ? ' ГиБ' : ' МиБ');
}

function exchangeTable(head, rows) {
  if (!rows.length) return '';
  return '<table><thead><tr>' + head.map(h=>'<th>'+h+'</th>').join('') +
    '</tr></thead><tbody>' + rows.map(cells=>'<tr>'+cells.map(v=>'<td class="wrap">'+v+'</td>').join('')+'</tr>').join('')+'</tbody></table>';
}

function showExchange(samples) {
  const probes = samples.filter(s => s.kind === 'probe');
  exchangeCollectors = samples.filter(s => s.kind === 'collector');
  exchangeNodes = new Map(probes.map(s => [s.url, s]));
  const missing = samples.filter(s => !s.data).length;
  $('#exchangeSummary').textContent = samples.length
    ? 'Ответили ' + (samples.length-missing) + ' из ' + samples.length + ' узлов · обновлено ' + fmt(new Date())
    : 'Нет зарегистрированных узлов';
  const err = message => '<span class="bad">'+escHtml(message)+'</span>';
  const count = n => n == null ? '—' : num(n);
  const batch = (event, units) => event ? fmt(event.at) + ' · ' + count(event.count ?? event.results) + ' ' + units +
    '<br><small>' + escHtml(event.batch) + '</small>' : '—';
  $('#exchangeProbes').innerHTML = exchangeTable(
    ['Проба','Ждут подтверждения, результатов','Собирает сейчас','Последнее подтверждение на пробе','results.db / TaskInfo.json','Свободно на диске','Состояние'],
    probes.map(s => {
      const d = s.data;
      const registered = probesCache.find(p => p.requestInfo === s.url);
      return [escHtml(s.url),count(d?.pendingResults),escHtml(d?.ordered ? d.owner || 'нет' : registered?.owner || '—'),
        batch(d?.lastAck,'рез.')+(d?.lastAck?.collector ? '<br>'+escHtml(d.lastAck.collector) : ''),
        bytes(d?.resultsDbBytes)+' / '+bytes(d?.taskInfoBytes), d?.disk ? bytes(d.disk.availableBytes)+' из '+bytes(d.disk.totalBytes) : '—',
        s.error ? err(s.error) : exchangeStatus(registered ?? {requestInfo:s.url}).html];
    }));
  $('#exchangeCollectors').innerHTML = exchangeTable(
    ['Съёмник','В буфере, строк','Ждут подтверждения, пачек','Выгружено строк с запуска','ClickHouse · адрес и таблица','Последний ответ / ошибка'],
    exchangeCollectors.map(s => {
      const st = s.data?.storage;
      return [escHtml(s.id)+'<br>'+escHtml(s.url), count(st?.pendingRows),count(st?.awaitingConfirmation),count(st?.rowsUploaded),
        st ? escHtml(st.url)+'<br>'+escHtml(st.target) : '—',s.error ? err(s.error) : fmt(s.at) + (st?.lastError ? '<br>'+err(st.lastError) : '')];
    }));
  const rows = exchangeCollectors.flatMap(s => Object.entries(s.data?.transfers ?? {}).map(([url,t]) =>
    [escHtml(url),escHtml(s.id),batch(t.read,'рез.'),batch(t.stored,'стр.'),
      t.stored ? escHtml(t.stored.endpoint)+'<br>'+escHtml(s.data.storage.target) : '—',
      t.error ? fmt(t.errorAt)+'<br>'+err(t.error) : '—']));
  $('#exchangeTransfers').innerHTML = exchangeTable(
    ['Проба','Съёмник','Последняя полученная пачка','Последняя запись в ClickHouse','Куда записана пачка','Ошибка обмена'], rows);
  renderProbes();
}

function loadExchange() {
  if (exchangeRequest) return exchangeRequest;
  exchangeRequest = jget('exchange').then(showExchange).catch(e => {
    showExchange([]);
    $('#exchangeSummary').textContent = 'Текущая статистика недоступна: ' + e.message;
  }).finally(() => { exchangeRequest = null; });
  return exchangeRequest;
}
