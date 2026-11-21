<div align="center">

<img src="Src/Assets/Icons/retrogc.png" alt="Retro Game Card logo" width="160" />

# 🛠️ Retro Game Card: Creator

### Make [Retro Game Card](https://github.com/retrogc/retrogc-launcher) game cards, on Windows and Linux

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Avalonia](https://img.shields.io/badge/UI-Avalonia%2012-8B44AC?style=flat-square)](https://avaloniaui.net/)
[![License](https://img.shields.io/badge/License-GPL--3.0-blue?style=flat-square)](LICENSE)

</div>

---

A desktop app to create and edit game cards without writing JSON or using the command line:

- 📂 **Open a card folder** (or a `.rgc` file): every field is filled from its definition.
- 🆕 **Start a new card** in an empty folder.
- 💽 **Open a card image** (`.img.xz` or `.img`): just view it (read-only), or extract it to a folder
  to edit and rebuild it. Write to USB works straight from a viewed image.
- ✏️ **Edit every field**: name, description, platform, year, developer, genre, players, cover,
  RetroArch core / Recalbox system / ROMs / BIOS files, and the optional MiSTer FPGA section.
- 📥 **Add the files**: pick a ROM, BIOS, core or cover anywhere on your disk and it is copied into
  the card at the right place. Missing files are listed, one click fills them.
- 💿 **Build the image**: a `.img.xz` FAT32 image labelled `RETROGC` on a read-only GPT partition, the same as
  `retrogc-image.sh --build`, ready for balenaEtcher.
- 🔌 **Write to USB**: flash the card straight to a USB stick.

Fields the app doesn't know are kept when saving, so cards stay compatible with newer formats.

## 🖥️ Platforms

| | Platform | Build image | Open image | Write to USB |
|:-:|---|:-:|:-:|---|
| 🪟 | **Windows** x64 | ✅ | ✅ | ✅ asks for administrator rights (UAC) |
| 🐧 | **Linux** x64 | ✅ | ✅ | ✅ asks for your password (polkit / `pkexec`), needs `udisksctl` |

Images are built and read entirely by the app (its own FAT writer / reader and a bundled xz
library): no `mkfs`, `mtools` or other tools are needed, on either system.

## 🚀 Usage

1. Download `retrogc-creator` for your system from the
   [**releases page**](https://github.com/retrogc/retrogc-creator/releases) and run it
   (on Linux: `chmod +x retrogc-creator` first).
2. **Open folder** (a card folder with its `.rgc`), **Open image** (a card `.img.xz`, see below) or
   **New card** (an empty folder).
3. Fill in the fields. Use **Choose file…** / **Add files…** to bring the game files in: they are
   copied into the card (`games/<system>/` for ROMs and BIOS, `_Console/` or `_Arcade/` for MiSTer).
4. When the sidebar says **Ready to build**, press **Build image** (saves the card first), then
   **Write to USB**, or flash the `.img.xz` with [balenaEtcher](https://etcher.balena.io/).

### 💽 Opening a card image

**Open image** reads any game card image (`.img.xz` or raw `.img`), whether it was made by the
creator, by [`retrogc-image.sh`](https://github.com/retrogc/retrogc-launcher/blob/main/Platforms/Linux/retrogc-image.sh)
or downloaded from a release. Then choose:

| Choice | What happens |
|---|---|
| 📂 **Extract to a folder…** | The card is unpacked into a new folder (named after the image) and opens like any card folder: edit it, add files, rebuild it. |
| 👀 **Just view** | The card is unpacked to a temporary folder and opens **read-only**. **Write to USB** flashes the image as is (no rebuild), and **Extract…** turns it into an editable copy. The temporary folder is deleted when you close the card. |

FAT12, FAT16 and FAT32 images are supported (GPT or no partition table). Installer cards
(exFAT) aren't game cards and can't be opened.

The `.rgc` format itself is documented in the
[launcher README](https://github.com/retrogc/retrogc-launcher#-the-rgc-card-format).

### ⌨️ Command line

```bash
retrogc-creator <card folder | card.rgc>                  # open a card
retrogc-creator --build <card folder> <output.img.xz>     # build an image without the window
retrogc-creator --extract <image.img.xz> <folder>         # unpack a card image without the window
```

## 🧑‍💻 Building from source

> Requires the [**.NET 10 SDK**](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/retrogc/retrogc-creator.git
cd retrogc-creator
dotnet run
```

```text
Src/
├── 📁 Models/        CardDocument: the .rgc as a JSON tree (keeps unknown fields)
├── 📁 Services/      Fat32ImageWriter / FatImageReader, ImageBuilder / ImageExtractor (xz),
│                     CardFolder, UsbDrives, Flasher
├── 📁 ViewModels/    MVVM view models (CommunityToolkit.Mvvm)
├── 📁 Views/         Main window, USB writer window
└── 📁 Styles/        Theme shared with the launcher
```

Release builds are self-contained, trimmed, single-file executables, built by the
[**release workflow**](.github/workflows/release.yml).

## 📄 License

Free software under the [**GNU General Public License v3.0**](LICENSE).

<div align="center">

<sub>Retro Game Card ships no games. Only use ROMs you legally own.</sub>

</div>
