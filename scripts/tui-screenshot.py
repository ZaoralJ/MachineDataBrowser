# /// script
# requires-python = ">=3.11"
# dependencies = ["pyte>=0.8", "pillow>=10", "fonttools>=4"]
# ///
"""Renders `mdbrowser tui` screenshots to docs/images/tui*.png: the real TUI in a pseudo-terminal, driven by keys.

Needs the MQTT and Logix simulators (`just all`) and builds the CLI first (see `just docs-tui-screenshot`).
Nothing is written to the simulators: the write prompt is cancelled.
Usage: uv run scripts/tui-screenshot.py <mdbrowser> <images folder>
"""

import fcntl
import io
import os
import pty
import re
import select
import signal
import struct
import sys
import termios
import tempfile
import time
import urllib.request
import zipfile
from pathlib import Path

import pyte
from fontTools.ttLib import TTFont
from PIL import Image, ImageDraw, ImageFont

COLS, ROWS = 150, 45
FONT_SIZE = 15
MQTT = "mqtt://127.0.0.1:1883"
LOGIX = "eip://127.0.0.1:44818/1,0"
SHIFT_DOWN, RIGHT, DOWN, TAB, ESC = "\x1b[1;2B", "\x1b[C", "\x1b[B", "\t", "\x1b"
CACHE = Path(os.environ.get("XDG_CACHE_HOME", Path.home() / ".cache")) / "mdbrowser-docs-fonts"
FONTS = {
    # Regular, bold; DejaVu has the braille dots of the trend chart that JetBrains Mono lacks.
    "JetBrainsMono": ("https://github.com/JetBrains/JetBrainsMono/releases/download/v2.304/JetBrainsMono-2.304.zip",
                      "fonts/ttf/JetBrainsMono-Regular.ttf", "fonts/ttf/JetBrainsMono-Bold.ttf"),
    "DejaVuSansMono": ("https://github.com/dejavu-fonts/dejavu-fonts/releases/download/version_2_37/dejavu-fonts-ttf-2.37.zip",
                       "dejavu-fonts-ttf-2.37/ttf/DejaVuSansMono.ttf", "dejavu-fonts-ttf-2.37/ttf/DejaVuSansMono-Bold.ttf"),
}


def fonts() -> list[tuple[ImageFont.FreeTypeFont, ImageFont.FreeTypeFont, set[int]]]:
    """(regular, bold, code points) per font, in fallback order; downloaded once into the cache."""
    result = []
    for name, (url, regular, bold) in FONTS.items():
        folder = CACHE / name
        if not (folder / Path(bold).name).exists():
            folder.mkdir(parents=True, exist_ok=True)
            with urllib.request.urlopen(url) as response, zipfile.ZipFile(io.BytesIO(response.read())) as archive:
                for member in (regular, bold):
                    (folder / Path(member).name).write_bytes(archive.read(member))
        regular_path, bold_path = folder / Path(regular).name, folder / Path(bold).name
        cmap = set(TTFont(regular_path).getBestCmap())
        result.append((ImageFont.truetype(str(regular_path), FONT_SIZE), ImageFont.truetype(str(bold_path), FONT_SIZE), cmap))
    return result


class Terminal:
    """The TUI in a pseudo-terminal of COLS x ROWS, its output kept in a pyte screen."""

    def __init__(self, mdbrowser: str, endpoint: str):
        self.screen = pyte.Screen(COLS, ROWS)
        self.stream = pyte.ByteStream(self.screen)
        env = dict(os.environ, TERM="xterm-256color", COLORTERM="truecolor", LANG="en_US.UTF-8",
                   # A fresh data folder: no saved TUI layout or theme of the person running it.
                   MACHINEDATABROWSER_DATA_DIR=tempfile.mkdtemp(prefix="mdb-docs-"))
        self.pid, self.fd = pty.fork()
        if self.pid == 0:
            os.execve(mdbrowser, [mdbrowser, "tui", endpoint, "--theme", "Indigo"], env)
        fcntl.ioctl(self.fd, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))

    def pump(self, seconds: float) -> None:
        end = time.time() + seconds
        while time.time() < end:
            ready, _, _ = select.select([self.fd], [], [], 0.05)
            if not ready:
                continue
            data = os.read(self.fd, 65536)
            self.stream.feed(data)
            # Terminal.Gui asks for the cursor position and device attributes at startup.
            if b"\x1b[6n" in data:
                os.write(self.fd, b"\x1b[1;1R")
            if b"\x1b[c" in data or b"\x1b[0c" in data:
                os.write(self.fd, b"\x1b[?62;c")

    def text(self) -> str:
        return "\n".join(self.screen.display)

    def wait_for(self, text: str, seconds: float = 30) -> None:
        end = time.time() + seconds
        while text not in self.text():
            if time.time() > end:
                sys.exit(f"timed out waiting for {text!r}; screen:\n{self.text()}")
            self.pump(0.2)

    def keys(self, keys: str, pause: float = 0.4) -> None:
        os.write(self.fd, keys.encode())
        self.pump(pause)

    def search(self, text: str, expect: str) -> None:
        """s, the text, then Enter on the first match: the tree goes to it."""
        self.keys("s", 0.8)
        self.keys(text + "\r", 2.0)
        self.wait_for(expect)
        self.keys("\r", 1.5)

    def close(self) -> None:
        os.kill(self.pid, signal.SIGKILL)


def color(value: str, fallback: str) -> tuple[int, int, int]:
    value = value if len(value) == 6 and all(c in "0123456789abcdefABCDEF" for c in value) else fallback
    return tuple(int(value[i:i + 2], 16) for i in (0, 2, 4))


def render(screen: pyte.Screen, path: Path) -> None:
    faces = fonts()
    regular = faces[0][0]
    left, top, right, bottom = regular.getbbox("M")
    cell_w = round(regular.getlength("M"))
    ascent, descent = regular.getmetrics()
    cell_h = ascent + descent + 2
    image = Image.new("RGB", (COLS * cell_w, ROWS * cell_h))
    draw = ImageDraw.Draw(image)
    for y in range(ROWS):
        row = screen.buffer[y]
        for x in range(COLS):
            cell = row[x]
            fg, bg = color(cell.fg, "c5c8d2"), color(cell.bg, "0b0c10")
            if cell.reverse:
                fg, bg = bg, fg
            box = (x * cell_w, y * cell_h, (x + 1) * cell_w, (y + 1) * cell_h)
            draw.rectangle(box, fill=bg)
            if not cell.data or cell.data == " ":
                continue
            point = ord(cell.data[0])
            face = next((f for f in faces if point in f[2]), faces[0])
            font = face[1] if cell.bold else face[0]
            if 0x2500 <= point <= 0x257F:
                # Box lines stretch over the whole cell, so borders join up.
                draw_box(draw, cell.data, box, fg)
            elif 0x2800 <= point <= 0x28FF:
                draw_braille(draw, point - 0x2800, box, fg)
            else:
                draw.text((box[0], box[1] + 1), cell.data, font=font, fill=fg)
            if cell.underscore:
                draw.line((box[0], box[3] - 2, box[2] - 1, box[3] - 2), fill=fg)
    image.save(path, optimize=True)


def draw_box(draw: ImageDraw.ImageDraw, char: str, box: tuple[int, int, int, int], fill: tuple[int, int, int]) -> None:
    """The box-drawing characters the TUI uses, as lines through the cell centre (rounded corners drawn square)."""
    x0, y0, x1, y1 = box
    cx, cy = (x0 + x1) // 2, (y0 + y1) // 2
    arms = {  # left, right, up, down
        "─": "lr", "│": "ud", "╭": "rd", "┌": "rd", "╮": "ld", "┐": "ld", "╰": "ru", "└": "ru", "╯": "lu", "┘": "lu",
        "├": "rud", "┤": "lud", "┬": "lrd", "┴": "lru", "┼": "lrud",
    }.get(char)
    if arms is None:
        return
    for arm in arms:
        end = {"l": (x0, cy), "r": (x1 - 1, cy), "u": (cx, y0), "d": (cx, y1 - 1)}[arm]
        draw.line(((cx, cy), end), fill=fill)


def draw_braille(draw: ImageDraw.ImageDraw, bits: int, box: tuple[int, int, int, int], fill: tuple[int, int, int]) -> None:
    """The trend chart's braille dots (2 x 4 per cell), drawn directly: fonts with them are rare and differ."""
    x0, y0, x1, y1 = box
    # Dot n (bit n-1) at (column, row): 1-3 and 7 on the left, 4-6 and 8 on the right.
    places = [(0, 0), (0, 1), (0, 2), (1, 0), (1, 1), (1, 2), (0, 3), (1, 3)]
    w, h = (x1 - x0) / 2, (y1 - y0) / 4
    r = max(1, round(min(w, h) * 0.28))
    for bit, (col, row) in enumerate(places):
        if bits & (1 << bit):
            cx, cy = x0 + w * (col + 0.5), y0 + h * (row + 0.5)
            draw.ellipse((cx - r, cy - r, cx + r - 1, cy + r - 1), fill=fill)


def theme_names() -> list[str]:
    """The colour themes in the order t steps through them, from the catalog the app and the TUI share."""
    catalog = Path(__file__).resolve().parent.parent / "src/MachineDataBrowser.Core/ColorThemeCatalog.cs"
    return re.findall(r'^\s*new\("(\w+)", "', catalog.read_text(encoding="utf-8"), re.MULTILINE)


def mqtt_scenes(mdbrowser: str, images: Path) -> None:
    """The main view on MQTT machine data, then the same with panes hidden, one pane alone, and the help."""
    terminal = Terminal(mdbrowser, MQTT)
    try:
        terminal.wait_for("Connected")
        terminal.wait_for("Topics")
        # The status topic of machine m1 and its parts, with status/speed selected for the trend.
        terminal.search("m1", "temperature")
        terminal.wait_for("NodeId")
        terminal.keys(RIGHT, 1.0)
        terminal.keys(DOWN, 0.5)
        terminal.keys("m", 2.0)
        terminal.wait_for("Monitored Items (")
        terminal.keys(TAB, 0.5)
        terminal.keys(TAB, 0.5)
        terminal.keys(DOWN, 0.5)
        # Enough samples for the trend to show its shape.
        terminal.pump(20)
        render(terminal.screen, images / "tui.png")

        # 2 and 4 hide Attributes and Trend; the others take their room.
        terminal.keys("2", 0.5)
        terminal.keys("4", 1.5)
        render(terminal.screen, images / "tui-panes.png")
        terminal.keys("2", 0.5)
        terminal.keys("4", 0.5)

        # Shift+3: Monitored Items alone on the screen.
        terminal.keys("#", 2.0)
        render(terminal.screen, images / "tui-zoom.png")
        terminal.keys("#", 1.0)

        terminal.keys("h", 1.5)
        terminal.wait_for("Keys")
        render(terminal.screen, images / "tui-help.png")
        terminal.keys(ESC, 0.8)

        # Every colour theme, dark and light: t goes to the next theme (catalog order), l switches light and dark.
        (images / "themes").mkdir(exist_ok=True)
        for index, name in enumerate(theme_names()):
            if index > 0:  # the first is the one it started with (--theme Indigo), not logged
                terminal.wait_for(f"Theme {name}, dark")
            render(terminal.screen, images / "themes" / f"tui-{name.lower()}-dark.png")
            terminal.keys("l", 1.5)
            terminal.wait_for(f"Theme {name}, light")
            render(terminal.screen, images / "themes" / f"tui-{name.lower()}-light.png")
            terminal.keys("l", 1.0)
            terminal.keys("t", 1.5)
    finally:
        terminal.close()


def logix_scenes(mdbrowser: str, images: Path) -> None:
    """Several array tags selected and monitored, the full value of a large array, and the write prompt."""
    terminal = Terminal(mdbrowser, LOGIX)
    try:
        terminal.wait_for("Connected")
        terminal.wait_for("Controller Tags")
        terminal.search("Arrays", "Arrays")
        terminal.keys(RIGHT, 2.0)
        terminal.wait_for("Bools")
        terminal.keys(DOWN, 0.5)
        for _ in range(5):
            terminal.keys(SHIFT_DOWN, 0.3)
        terminal.keys("m", 3.0)
        terminal.wait_for("Monitored Items (6)")
        terminal.pump(2)
        render(terminal.screen, images / "tui-multiselect.png")

        # v on a tag in the tree: its whole value, here 5000 numbers, in a scrollable popup.
        terminal.search("BigDintArray", "BigDintArray")
        terminal.keys("v", 3.0)
        terminal.wait_for("500, ")
        render(terminal.screen, images / "tui-value.png")
        terminal.keys(ESC, 1.0)

        # w shows the current value and asks for the new one; Esc cancels, so nothing is written.
        terminal.search("TestDint", "TestDint")
        terminal.keys("w", 2.0)
        terminal.wait_for("Now:")
        terminal.keys("12345", 1.0)
        render(terminal.screen, images / "tui-write.png")
        terminal.keys(ESC, 1.0)
    finally:
        terminal.close()


def main() -> None:
    mdbrowser, images = sys.argv[1], Path(sys.argv[2])
    mqtt_scenes(mdbrowser, images)
    logix_scenes(mdbrowser, images)


if __name__ == "__main__":
    main()
