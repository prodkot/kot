"""Self-contained review page, using production component and trajectories."""
from pathlib import Path
import base64
r=Path(__file__).resolve().parents[1];ui=r/'Windows/ui'
font=base64.b64encode((ui/'fonts/Manrope.ttf').read_bytes()).decode()
html='''<!doctype html><html lang="ru"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>kot. Живая точка</title><style>
@font-face{font-family:Manrope;src:url(data:font/ttf;base64,FONT);font-weight:200 800}
:root{--bg:#151718;--surface:#202425;--line:#303536;--text:#f2f4ed;--muted:#969e9f;--detail:#d4f58a;--ease:cubic-bezier(.22,1,.36,1)}
.light{--bg:#f6f6f2;--surface:#fff;--line:#dadcd5;--text:#252a23;--muted:#757d73;--detail:#55752a}
*{box-sizing:border-box}body{margin:0;padding:28px;background:var(--bg);color:var(--text);font:14px Manrope,sans-serif}main{max-width:1000px;margin:auto}h1{font-size:26px;margin:0 0 8px;letter-spacing:-1px}p{color:var(--muted);line-height:1.6;margin:0 0 22px}nav{display:flex;flex-wrap:wrap;gap:8px;margin:18px 0 24px}button{font:inherit;color:var(--text);border:1px solid var(--line);background:var(--surface);border-radius:9px;padding:9px 14px;cursor:pointer}button:hover{border-color:var(--detail)}button:focus-visible{outline:2px solid var(--detail);outline-offset:3px}.grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:12px}article{border:1px solid var(--line);border-radius:12px;background:var(--surface);padding:16px;min-width:0}h2{font-size:14px;margin:0;font-weight:650}small{color:var(--muted);font-size:11px}.stage{height:106px;display:flex;align-items:center;justify-content:center;gap:34px}.stage .tiny{font-size:24px}.caption{margin:0;font-size:12px}.egg{margin-top:12px;display:flex;align-items:center;gap:28px;padding:18px 24px}.egg .stage{width:100px;flex:none}.egg p{margin:6px 0 10px;font-size:12px}.sizes .stage .big{font-size:96px}footer{color:var(--muted);font-size:11px;margin-top:22px;line-height:1.6}@media(max-width:650px){body{padding:16px}.grid{grid-template-columns:repeat(2,minmax(0,1fr))}.stage{gap:16px}h1{font-size:23px}}@media(max-width:380px){.grid{grid-template-columns:1fr}.egg{flex-wrap:wrap}}
CSS
</style><main><h1>kot. У точки есть характер</h1><p>Небольшие движения для настоящего ожидания. Тот же компонент, что в приложении.</p><nav><button id="theme">Светлая тема</button><button id="speed">Скорость: 1×</button><button id="pause">Пауза</button><button id="size">Размер: 72 / 24</button></nav><div class="grid" id="grid"></div><article class="egg"><div class="stage"><span id="egg"></span></div><div><h2>47. Ну всё, я ушла</h2><p>Отдельная сценка для логотипа. Не участвует в загрузках.</p><button id="play">Показать сценку</button></div></article><footer>Спокойные 75% · Живые 20% · Шутки 4,5% · Редкие 0,5%.<br>В компактных местах только спокойные. Системное уменьшение движения сохраняет неподвижный знак. При скрытии страницы кадры не запрашиваются.</footer></main>
<script>MOTION</script><script>COMPONENT</script><script>
const descriptions={quiet:'Прежняя тихая точка. Для коротких операций.',breathe:'Почти незаметный перенос веса.',hop:'Гравитационная дуга и малый отскок.',roll:'Опора на угол, поворот через грань.',nudge:'Один контакт, короткий ответ буквы.',escape:'Два шага в сторону. И обратно.',impatient:'Смена опоры, затихающая к концу.',peek:'Чуть наклонилась: ну что там?',balance:'Редкий рискованный баланс.'};
window.players=[];window.demoPlayers={};
for(const [id,m] of Object.entries(KotLoadingMotion.motions)){
 if(id==='protest')continue;
 const card=document.createElement('article');card.innerHTML='<h2>'+m.title+'</h2><div class="stage"><span class="big"></span><span class="tiny"></span></div><p class="caption">'+descriptions[id]+'</p>';grid.append(card);
 for(const node of card.querySelectorAll('.stage span')){const p=KotLoading.create(node,{variant:id});p.start();players.push(p);if(node.className.includes('big'))demoPlayers[id]=p;}
}
const eggPlayer=KotLoading.create(egg,{variant:'protest',once:true});players.push(eggPlayer);demoPlayers.protest=eggPlayer;
play.onclick=()=>eggPlayer.start();
let slow=false,paused=false;
theme.onclick=()=>{document.documentElement.classList.toggle('light');theme.textContent=document.documentElement.classList.contains('light')?'Тёмная тема':'Светлая тема';};
speed.onclick=()=>{slow=!slow;players.forEach(p=>p.speed(slow?.25:1));speed.textContent='Скорость: '+(slow?'0,25×':'1×');};
pause.onclick=()=>{paused=!paused;window.dispatchEvent(new CustomEvent('kot-window-visibility',{detail:!paused}));pause.textContent=paused?'Продолжить':'Пауза';};
size.onclick=()=>{document.body.classList.toggle('sizes');size.textContent=document.body.classList.contains('sizes')?'Размер: 96 / 24':'Размер: 72 / 24';};
</script></html>'''
html=html.replace('FONT',font).replace('CSS',(ui/'styles/loading.css').read_text()).replace('MOTION',(ui/'loading-motion.js').read_text()).replace('COMPONENT',(ui/'loading-ui.js').read_text())
out=r/'artifacts/loading-demo.html';out.parent.mkdir(exist_ok=True);out.write_text(html);print(out)
