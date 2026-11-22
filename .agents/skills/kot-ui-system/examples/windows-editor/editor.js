const scenes = [
  {title: "Пролог", seconds: 3},
  {title: "Линии города", seconds: 5},
  {title: "Эпилог", seconds: 4}
];
const byId = id => document.getElementById(id);
const timestamp = seconds => "00:" + String(seconds).padStart(2, "0");
let selected = 0;
const clips = scenes.map((scene, index) => {
  const button = document.createElement("button");
  button.className = "clip";
  button.style.setProperty("--editor-seconds", scene.seconds);
  button.onclick = () => select(index);
  byId("clips").append(button);
  return button;
});
function select(index) {
  selected = index;
  let start = 0;
  scenes.forEach((scene,i) => {
    const active = i === index;
    const time = document.createElement("time");
    time.textContent = timestamp(start);
    clips[i].replaceChildren(time, (active ? "✓ " : "") + scene.title);
    clips[i].setAttribute("aria-pressed", String(active));
    if (active) {
      byId("time").textContent = time.textContent;
      byId("card-number").textContent = String(i + 1).padStart(2,"0") + " / 03";
      byId("card-text").textContent = scene.title;
    }
    start += scene.seconds;
  });
  byId("prev").disabled = index === 0;
  byId("next").disabled = index === scenes.length - 1;
}
byId("prev").onclick = () => select(selected - 1);
byId("next").onclick = () => select(selected + 1);
function setTheme(light) {
  document.documentElement.classList.toggle("theme-light", light);
  byId("theme").textContent = light ? "Тёмная тема" : "Светлая тема";
  window.chrome?.webview?.postMessage(light ? "light" : "dark");
}
byId("theme").onclick = () => setTheme(!document.documentElement.classList.contains("theme-light"));
const dialog = document.querySelector("dialog");
byId("export").onclick = () => dialog.showModal();
byId("save").onclick = () => {
  const url = URL.createObjectURL(new Blob([JSON.stringify(scenes,null,2)],{type:"application/json"}));
  const link = document.createElement("a");
  link.href = url;
  link.download = "монтажный-лист.json";
  link.click();
  setTimeout(() => URL.revokeObjectURL(url),1000);
};
setTheme(document.documentElement.classList.contains("theme-light"));
select(selected);
