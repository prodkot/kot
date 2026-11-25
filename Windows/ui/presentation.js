/* Safe text/flag presentation; never interpret subscription labels as HTML. */
'use strict';
window.KotPresentation = (() => {
  const flagCodes = new Set('ad ae af ag ai al am ao aq ar as at au aw ax az ba bb bd be bf bg bh bi bj bl bm bn bo bq br bs bt bv bw by bz ca cc cd cf cg ch ci ck cl cm cn co cp cr cu cv cw cx cy cz de dg dj dk dm do dz ec ee eg eh er es et eu fi fj fk fm fo fr ga gb gd ge gf gg gh gi gl gm gn gp gq gr gs gt gu gw gy hk hm hn hr ht hu ic id ie il im in io iq ir is it je jm jo jp ke kg kh ki km kn kp kr kw ky kz la lb lc li lk lr ls lt lu lv ly ma mc md me mf mg mh mk ml mm mn mo mp mq mr ms mt mu mv mw mx my mz na nc ne nf ng ni nl no np nr nu nz om pa pc pe pf pg ph pk pl pm pn pr ps pt pw py qa re ro rs ru rw sa sb sc sd se sg sh si sj sk sl sm sn so sr ss st sv sx sy sz tc td tf tg th tj tk tl tm tn to tr tt tv tw tz ua ug um un us uy uz va vc ve vg vi vn vu wf ws xk xx ye yt za zm zw'.split(' '));
  const flagPattern = /[\u{1F1E6}-\u{1F1FF}]{2}/gu;
  function flagCode(emoji) {
    return [...emoji].map(c=>String.fromCharCode(c.codePointAt(0)-0x1F1E6+97)).join('');
  }
  function flagImage(code, fallback=()=>document.createTextNode(code.toUpperCase())) {
    const image=document.createElement('img');image.className='country-flag';
    image.src='flags/'+code+'.svg';image.alt=code.toUpperCase();image.draggable=false;
    image.addEventListener('error',()=>image.replaceWith(fallback()),{once:true});
    return image;
  }
  function label(el, value) {
    const text=String(value??'');if(el.dataset.label===text)return;
    const parts=[];let offset=0;
    for(const match of text.matchAll(flagPattern)) {
      const code=flagCode(match[0]);parts.push(document.createTextNode(text.slice(offset,match.index)));
      parts.push(flagCodes.has(code)?flagImage(code):document.createTextNode(code.toUpperCase()));
      offset=match.index+match[0].length;
    }
    parts.push(document.createTextNode(text.slice(offset)));el.replaceChildren(...parts);el.dataset.label=text;
  }
  function serverPresentation(node) {
    let code='';
    const name=String(node?.name??'').replace(/([\u{1F1E6}-\u{1F1FF}]{2})\uFE0F?/gu,(_,emoji)=>{
      const candidate=flagCode(emoji);if(!code&&flagCodes.has(candidate))code=candidate;return '';
    }).replace(/[ \t]{2,}/g,' ').trim();
    // Native node.code is a protocol (TR, SS, VL), not an ISO country code.
    const supplied=String(node?.countryCode||'').toLowerCase();
    return {name:name||'Сервер',code:code||(flagCodes.has(supplied)?supplied:'')};
  }
  function countryFallback() {
    const icon=document.createElementNS('http://www.w3.org/2000/svg','svg');
    icon.classList.add('country-fallback');icon.setAttribute('viewBox','0 0 20 20');
    icon.setAttribute('role','img');icon.setAttribute('aria-label','Без флага');
    const circle=document.createElementNS('http://www.w3.org/2000/svg','circle');
    circle.setAttribute('cx','10');circle.setAttribute('cy','10');circle.setAttribute('r','7');
    circle.setAttribute('fill','#f2f4f1');circle.setAttribute('stroke','#c7cdc8');icon.append(circle);return icon;
  }
  function countryBadge(el, node, automatic=false) {
    const code=serverPresentation(node).code;
    const key=automatic?'auto':code||'circle';
    if(el.dataset.badge===key)return;
    el.replaceChildren(automatic?document.createTextNode('A'):code?flagImage(code,countryFallback):countryFallback());
    el.dataset.badge=key;
  }
  function bytes(n,rate=false){if(n===null||n===undefined||!Number.isFinite(n))return 'Нет данных';const units=['Б','КБ','МБ','ГБ','ТБ'];let i=0;while(n>=1024&&i<units.length-1){n/=1024;i++;}return new Intl.NumberFormat('ru-RU',{maximumFractionDigits:i===0?0:1}).format(n)+' '+units[i]+(rate?'/с':'');}
  return {label,serverPresentation,countryBadge,bytes};
})();
