"""Three new UI examples, using kot primitives on an isolated Chromium page."""
from pathlib import Path

from playwright.sync_api import expect, sync_playwright

UI = Path(__file__).resolve().parents[4] / "Windows/ui"

PAGE = """
<button id="limitButton">Порог экспорта</button>
<section aria-label="Экспорт">
  <label for="exportProgress">Экспорт</label>
  <progress class="progress-track" id="exportProgress" max="100"></progress>
  <span role="status" id="exportStatus">Подготовка</span>
</section>
<dialog id="limit" aria-labelledby="title" data-dirty-form class="dialog">
  <h2 id="title">Порог экспорта</h2>
  <label>Размер <input id="limitValue" type="number" value="10"></label>
  <button id="back">Закрыть</button>
</dialog>
<dialog id="discardDialog" class="dialog" aria-label="Закрыть без сохранения?">
  <button id="closeDiscard">Закрыть</button>
  <button id="keepEditing">Продолжить редактирование</button>
  <button id="discardChanges">Сбросить</button>
</dialog>
"""

WIRING = """
() => {
  const find = id => document.getElementById(id);
  const form = find('limit');
  const dialogs = KotDialogUI({
    rememberFocus() {
      const trigger = document.activeElement;
      return () => trigger;
    },
    onStackChange() {}
  });
  find('limitButton').onclick = () => {
    if (!form.open) find('limitValue').value = '10';
    dialogs.open(form);
  };
  find('back').onclick = () => dialogs.requestClose(form);
  for (const dialog of document.querySelectorAll('dialog')) {
    dialog.oncancel = event => {
      event.preventDefault();
      dialogs.requestClose(dialog);
    };
  }
  window.closeLimit = () => dialogs.close(form);
  window.renderExport = ({done, total}) => {
    const bar = find('exportProgress');
    if (total > 0) {
      bar.max = total;
      bar.value = done;
    } else {
      bar.removeAttribute('value');
    }
    find('exportStatus').textContent = total > 0 ? done + ' из ' + total : 'Подготовка';
  };
}
"""


def component(page):
    bar = page.get_by_role("progressbar", name="Экспорт")
    for theme in ("dark", "light"):
        page.locator("html").evaluate("(el, light) => el.classList.toggle('theme-light', light)", theme == "light")
        assert bar.evaluate("el => getComputedStyle(el).colorScheme") == theme
        for done, total, fraction in [(0, 0, -1), (0, 100, 0), (72, 120, .6), (0, 0, -1)]:
            page.evaluate("value => renderExport(value)", dict(done=done, total=total))
            assert bar.evaluate("el => el.position") == fraction
            expect(page.get_by_role("status")).to_have_text(f"{done} из {total}" if total else "Подготовка")


def form_and_focus(page):
    trigger = page.get_by_role("button", name="Порог экспорта")
    trigger.focus()
    trigger.press("Enter")
    field = page.get_by_label("Размер")
    field.fill("17")
    field.press("Escape")
    expect(page.locator("#keepEditing")).to_be_focused()
    page.keyboard.press("Escape")
    expect(field).to_have_value("17")
    expect(field).to_be_focused()
    field.press("Escape")
    page.locator("#discardChanges").click()
    expect(page.locator("#limit")).not_to_be_visible()
    expect(trigger).to_be_focused()
    trigger.press("Enter")
    expect(field).to_have_value("10")


def interrupted_motion(page):
    page.emulate_media(reduced_motion="no-preference")
    trigger = page.locator("#limitButton")
    trigger.click()
    page.evaluate("""() => {
      window.closes = 0;
      const dialog = document.querySelector('#limit');
      dialog.addEventListener('close', () => window.closes++);
      const entry = dialog.getAnimations();
      if (!entry.length) throw new Error('Missing entrance animation');
      for (const effect of entry) {
        effect.pause();
        effect.currentTime = 70;
      }
      const opacity = Number(getComputedStyle(dialog).opacity);
      if (!(opacity > 0 && opacity < 1)) throw new Error('Expected intermediate frame');
      closeLimit();
      const exit = dialog.getAnimations().find(a => !a.effect.pseudoElement);
      const firstFrame = exit?.effect.getKeyframes()[0];
      if (!firstFrame || Math.abs(Number(firstFrame.opacity) - opacity) > 0.001)
        throw new Error('Exit jumped from the visible frame');
      document.querySelector('#limitButton').click();
    }""")
    page.evaluate("() => Promise.allSettled(document.querySelector('#limit').getAnimations().map(a => a.finished))")
    dialog = page.locator("#limit")
    expect(dialog).to_be_visible()
    assert page.evaluate("window.closes") == 0
    page.evaluate("closeLimit()")
    page.emulate_media(reduced_motion="reduce")
    expect(dialog).not_to_be_visible()
    expect(trigger).to_be_focused()
    page.wait_for_function("window.closes === 1")
    assert dialog.evaluate("el => el.getAnimations().length") == 0


if __name__ == "__main__":
    with sync_playwright() as pw:
        browser = pw.chromium.launch()
        try:
            for scenario in (component, form_and_focus, interrupted_motion):
                page = browser.new_page(reduced_motion="reduce")
                try:
                    page.set_content(PAGE)
                    for css in ("tokens.css", "client.css"):
                        page.add_style_tag(path=UI / "styles" / css)
                    page.add_script_tag(path=UI / "dialog-ui.js")
                    page.evaluate(WIRING)
                    scenario(page)
                    print("PASS", scenario.__name__, flush=True)
                finally:
                    page.close()
        finally:
            browser.close()
