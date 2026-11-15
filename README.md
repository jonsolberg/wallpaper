# Wallpapers from jonsolberg.se

Apps that change your wallpaper to the free, original wallpapers published on
[jonsolberg.se](https://jonsolberg.se:8080/Pictures/) – a new picture every few hours, every day,
every week, or as soon as a new one is published. Each app picks the picture size that fits the screen.

| Platform | Folder | What it is |
|---|---|---|
| Android 10+ | [`android/`](android) | Gallery, favourites, automatic change of home and/or lock screen, a "Next wallpaper" shortcut and quick settings tile |
| Linux | [`linux/`](linux) | Tray app for GNOME (Ubuntu), KDE Plasma, Xfce, Cinnamon and MATE, packaged as a `.deb` |
| Windows | – | The Windows tray app is available on [jonsolberg.se](https://jonsolberg.se:8080/tools.html); its source will be added here |

## Download

**[Latest release](https://github.com/jonsolberg/wallpaper/releases/latest)** – `Wallpapers.apk` for Android and
`jonsolberg-wallpapers_<version>_all.deb` for Linux:

```
sudo apt install ./jonsolberg-wallpapers_*_all.deb
```

## Building

Every push builds both packages with GitHub Actions (`.github/workflows/build.yml`) and publishes them as a release.

- **Android:** `cd android && ./gradlew assembleRelease`. Release builds are signed with the key from the
  repository secrets `SIGNING_KEYSTORE_B64`, `SIGNING_STORE_PASSWORD`, `SIGNING_KEY_ALIAS` and `SIGNING_KEY_PASSWORD`;
  without them the APK is debug-signed.
- **Linux:** the `linux/` folder is the package tree; `dpkg-deb --root-owner-group -b linux out.deb` builds it
  (the workflow adds `Installed-Size`).

## Support

The apps and wallpapers are free, open source and have no ads. If you enjoy them, you can
[buy me a beer on Ko-fi](https://ko-fi.com/jonsolberg) 🍺

[![Buy me a beer on Ko-fi](https://img.shields.io/badge/Ko--fi-buy%20me%20a%20beer-FF5E5B?logo=ko-fi&logoColor=white)](https://ko-fi.com/jonsolberg)

## License

GNU General Public License v3.0 – see [LICENSE](LICENSE). The wallpapers themselves are © Jon Solberg.
