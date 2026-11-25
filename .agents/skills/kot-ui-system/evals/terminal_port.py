"""Independent Textual port. Run with Python and textual installed."""
import asyncio

from textual.app import App, ComposeResult
from textual.widgets import Button, Static


class ExportApp(App):
    BINDINGS = [("escape", "cancel_export", "Отменить")]

    def compose(self) -> ComposeResult:
        yield Static("Готов к экспорту", id="status")
        yield Button("Экспорт", id="start")
        yield Button("Отменить", id="cancel", disabled=True)

    def on_mount(self) -> None:
        for button in self.query(Button):
            button.active_effect_duration = 0
        self.session = 0
        self.pending = False
        self.query_one("#start", Button).focus()

    def begin(self) -> None:
        if self.pending:
            return
        self.session += 1
        self.pending = True
        self.query_one("#status", Static).update("Выполняется")
        self.query_one("#start", Button).disabled = True
        cancel = self.query_one("#cancel", Button)
        cancel.disabled = False
        cancel.focus()

    def finish(self, session: int, status: str) -> None:
        if not self.pending or session != self.session:
            return
        self.pending = False
        self.query_one("#status", Static).update(status)
        self.query_one("#cancel", Button).disabled = True
        start = self.query_one("#start", Button)
        start.disabled = False
        start.focus()

    def action_cancel_export(self) -> None:
        self.finish(self.session, "Отменено")

    def on_button_pressed(self, event: Button.Pressed) -> None:
        if event.button.id == "start":
            self.begin()
        else:
            self.action_cancel_export()


async def check() -> None:
    for theme in ("nord", "textual-light"):
        app = ExportApp()
        app.theme = theme
        async with app.run_test(size=(40, 16)) as pilot:
            status = app.query_one("#status", Static)
            await pilot.press("enter")
            first = app.session
            assert app.focused.id == "cancel" and str(status.content) == "Выполняется"
            await pilot.press("escape")
            assert app.focused.id == "start" and str(status.content) == "Отменено"
            await pilot.press("enter")
            assert app.session > first
            app.finish(first, "Готово")
            assert str(status.content) == "Выполняется" and app.focused.id == "cancel"
            app.finish(app.session, "Ошибка записи")
            await pilot.pause()
            assert str(status.content) == "Ошибка записи" and app.focused.id == "start"
            await pilot.press("enter")
            app.finish(app.session, "Готово")
            await pilot.pause()
            assert str(status.content) == "Готово" and app.focused.id == "start"
        print("PASS terminal port", theme)


if __name__ == "__main__":
    asyncio.run(check())
