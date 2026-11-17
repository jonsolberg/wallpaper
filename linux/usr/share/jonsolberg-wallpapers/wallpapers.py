#!/usr/bin/env python3
# Copyright (C) 2026 Jon Solberg. Licensed under the GNU GPL v3 or later (see LICENSE).
"""Wallpapers from jonsolberg.se - a tray app for Linux desktops (Ubuntu/GNOME first, also KDE, Xfce, Cinnamon, MATE).

Sits in the top bar, changes the desktop picture on a schedule and picks the picture size that fits the screen.
Click the icon for the menu; middle-click it for the next wallpaper.
"""
import fcntl, json, math, os, random, re, shutil, subprocess, sys, threading, time, urllib.parse, urllib.request
from datetime import datetime, timedelta

import warnings
warnings.filterwarnings("ignore", category=DeprecationWarning)
import gi
gi.require_version('Gtk', '3.0')
gi.require_version('GdkPixbuf', '2.0')
gi.require_version('Pango', '1.0')
from gi.repository import Gtk, Gdk, GdkPixbuf, GLib, Gio, Pango

Indicator = None
for _name in ('AyatanaAppIndicator3', 'AppIndicator3'):
    try:
        gi.require_version(_name, '0.1')
        Indicator = __import__('gi.repository', fromlist=[_name]).__dict__[_name]
        break
    except (ValueError, ImportError, KeyError):
        pass

VERSION = '1.4'
APP = 'jonsolberg-wallpapers'
BASE = 'https://jonsolberg.se:8080'
SITE_BASE = os.environ.get('JSW_BASE', BASE)
SIZES = ['1920x1080', '2560x1600', '3440x1440', '3840x2160']
SIZE_NAMES = {'1920x1080': '1920×1080 (16:9, Full HD)', '2560x1600': '2560×1600 (16:10, laptops)',
              '3440x1440': '3440×1440 (21:9, ultrawide)', '3840x2160': '3840×2160 (16:9, 4K)'}
DESKTOP_RE = re.compile(r'^(.+?)-(' + '|'.join(SIZES) + r')\.(jpe?g|png)$', re.I)
WEEKDAYS = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday']

HOME = os.path.expanduser('~')
CONF = os.path.join(os.environ.get('XDG_CONFIG_HOME', HOME + '/.config'), APP)
DATA = os.path.join(os.environ.get('XDG_DATA_HOME', HOME + '/.local/share'), APP)
CACHE = os.path.join(os.environ.get('XDG_CACHE_HOME', HOME + '/.cache'), APP)
AUTOSTART = os.path.join(os.environ.get('XDG_CONFIG_HOME', HOME + '/.config'), 'autostart', APP + '.desktop')
ICON = APP if os.path.exists('/usr/share/icons/hicolor/256x256/apps/' + APP + '.png') else 'preferences-desktop-wallpaper'
for d in (CONF, DATA, CACHE):
    os.makedirs(d, exist_ok=True)


def log(msg):
    try:
        f = os.path.join(CACHE, 'log.txt')
        with open(f, 'a', encoding='utf-8') as fh:
            fh.write(datetime.now().strftime('%Y-%m-%d %H:%M:%S') + '  ' + str(msg) + '\n')
        if os.path.getsize(f) > 200000:
            lines = open(f, encoding='utf-8').read().splitlines()[-1500:]
            open(f, 'w', encoding='utf-8').write('\n'.join(lines) + '\n')
    except OSError:
        pass


# ------------------------------------------------------------------ settings
DEFAULTS = dict(mode='daily', hours=3, hour=7, weekday='Monday', target='desktop', order='random', newFirst=True,
                favOnly=False, size='auto', notify=True, current='', lastChange=0, baseline=0, followed=0, notified=0,
                favs=[], hidden=[], done=[], ever=[], history=[], autostartAsked=False)


class Settings(dict):
    path = os.path.join(CONF, 'settings.json')

    def __init__(self):
        super().__init__(json.loads(json.dumps(DEFAULTS)))
        try:
            with open(self.path, encoding='utf-8') as fh:
                self.update(json.load(fh))
        except (OSError, ValueError):
            pass

    __getattr__ = dict.__getitem__

    def __setattr__(self, k, v):
        self[k] = v

    def save(self):
        tmp = self.path + '.tmp'
        with open(tmp, 'w', encoding='utf-8') as fh:
            json.dump(self, fh, indent=1)
        os.replace(tmp, self.path)


# ------------------------------------------------------------------ the wallpapers on the site
class Item:
    def __init__(self, slug, time_):
        self.slug, self.time, self.urls = slug, time_, {}
        t = re.sub(r'^(jonsolberg-)?(wallpaper-)?', '', slug).replace('-', ' ').replace('_', ' ')
        self.title = t[:1].upper() + t[1:]

    def thumb_url(self):
        u = self.urls.get('1920x1080') or next(iter(self.urls.values()))
        name = urllib.parse.unquote(u.rsplit('/', 1)[1])
        return SITE_BASE + '/thumbs/' + urllib.parse.quote(re.sub(r'\.[^.]+$', '.jpg', name))


def fetch(url, timeout=30):
    req = urllib.request.Request(url, headers={'User-Agent': 'jonsolberg-wallpapers/%s (Linux)' % VERSION})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.read()


def parse(raw):
    groups = {}
    for f in json.loads(raw):
        m = DESKTOP_RE.match(f.get('name', ''))
        if not m:
            continue
        slug = re.sub('^jonsolberg-', '', m.group(1))
        it = groups.get(slug)
        if not it:
            it = groups[slug] = Item(slug, int(f['time']))
        it.urls[m.group(2)] = SITE_BASE + f['url']
        it.time = min(it.time, int(f['time']))   # published when its first size appeared
    return sorted(groups.values(), key=lambda i: -i.time)


def load_list():
    raw = fetch(SITE_BASE + '/pictures.php')
    with open(os.path.join(CACHE, 'list.json'), 'wb') as fh:
        fh.write(raw)
    return parse(raw)


def cached_list():
    try:
        return parse(open(os.path.join(CACHE, 'list.json'), 'rb').read())
    except (OSError, ValueError):
        return []


def download(url, folder=os.path.join(DATA, 'pictures'), keep=40):
    os.makedirs(folder, exist_ok=True)
    f = os.path.join(folder, urllib.parse.unquote(url.rsplit('/', 1)[1]))
    if not os.path.exists(f):
        data = fetch(url, 120)
        with open(f + '.part', 'wb') as fh:
            fh.write(data)
        os.replace(f + '.part', f)
        old = sorted((os.path.join(folder, x) for x in os.listdir(folder)), key=os.path.getmtime, reverse=True)
        for o in old[keep:]:
            try:
                os.remove(o)
            except OSError:
                pass
    else:
        os.utime(f)
    return f


# ------------------------------------------------------------------ screen and size
def screen_px():
    """Size of the largest monitor in real pixels."""
    best = None
    try:
        disp = Gdk.Display.get_default()
        for i in range(disp.get_n_monitors()):
            m = disp.get_monitor(i)
            g, s = m.get_geometry(), m.get_scale_factor()
            w, h = g.width * s, g.height * s
            if best is None or w * h > best[0] * best[1]:
                best = (w, h)
    except Exception as e:
        log('screen size: %s' % e)
    return best or (1920, 1080)


def best_size(sizes, sw, sh):
    """The closest shape first, then the smallest one that is at least as wide as the screen."""
    if not sizes:
        return '1920x1080'
    want = math.log(max(sw, sh) / max(1, min(sw, sh)))
    rows = []
    for k in sizes:
        w, h = map(int, k.split('x'))
        rows.append((abs(math.log(w / h) - want), w, k))
    dmin = min(r[0] for r in rows)
    same = sorted((r for r in rows if r[0] - dmin < .02), key=lambda r: r[1])
    fit = [r for r in same if r[1] >= max(sw, sh)]
    return (fit[0] if fit else same[-1])[2]


# ------------------------------------------------------------------ setting the picture
def desktop_env():
    return (os.environ.get('XDG_CURRENT_DESKTOP', '') + ':' + os.environ.get('DESKTOP_SESSION', '')).lower()


def run(*cmd):
    try:
        subprocess.run(cmd, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, timeout=20)
        return True
    except (OSError, subprocess.SubprocessError) as e:
        log('%s failed: %s' % (cmd[0], e))
        return False


def gset(schema, key, value):
    try:
        src = Gio.SettingsSchemaSource.get_default()
        if src is None or src.lookup(schema, True) is None or not src.lookup(schema, True).has_key(key):
            return False
        s = Gio.Settings.new(schema)
        s.set_string(key, value)
        Gio.Settings.sync()
        return True
    except Exception as e:
        log('gsettings %s %s: %s' % (schema, key, e))
        return False


def set_wallpaper(path, target):
    uri = 'file://' + urllib.parse.quote(path)
    de = desktop_env()
    desk, lock = target in ('desktop', 'both'), target in ('lock', 'both')
    if 'kde' in de or 'plasma' in de:
        if desk and shutil.which('plasma-apply-wallpaperimage'):
            run('plasma-apply-wallpaperimage', path)
        if lock and shutil.which('kwriteconfig6' if shutil.which('kwriteconfig6') else 'kwriteconfig5'):
            kw = 'kwriteconfig6' if shutil.which('kwriteconfig6') else 'kwriteconfig5'
            run(kw, '--file', 'kscreenlockerrc', '--group', 'Greeter', '--group', 'Wallpaper', '--group',
                'org.kde.image', '--group', 'General', '--key', 'Image', uri)
        return
    if 'xfce' in de and shutil.which('xfconf-query'):
        if desk:
            out = subprocess.run(['xfconf-query', '-c', 'xfce4-desktop', '-l'], capture_output=True, text=True).stdout
            for prop in (p for p in out.split() if p.endswith('/last-image')):
                run('xfconf-query', '-c', 'xfce4-desktop', '-p', prop, '-s', path)
                run('xfconf-query', '-c', 'xfce4-desktop', '-p', prop.replace('last-image', 'image-style'), '-s', '5')
        return
    if 'cinnamon' in de:
        if desk:
            gset('org.cinnamon.desktop.background', 'picture-uri', uri)
            gset('org.cinnamon.desktop.background', 'picture-options', 'zoom')
        return
    if 'mate' in de:
        if desk:
            gset('org.mate.background', 'picture-filename', path)
            gset('org.mate.background', 'picture-options', 'zoom')
        return
    # GNOME, Ubuntu, Budgie, Unity and most others
    done = False
    if desk:
        done = gset('org.gnome.desktop.background', 'picture-uri', uri)
        gset('org.gnome.desktop.background', 'picture-uri-dark', uri)
        gset('org.gnome.desktop.background', 'picture-options', 'zoom')
    if lock:
        done = gset('org.gnome.desktop.screensaver', 'picture-uri', uri) or done
        gset('org.gnome.desktop.screensaver', 'picture-options', 'zoom')
    if desk and not done and shutil.which('feh'):
        run('feh', '--bg-fill', path)


def notify(title, body):
    if shutil.which('notify-send'):
        subprocess.Popen(['notify-send', '-a', 'Wallpapers', '-i', ICON, title, body],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def follow_dark_mode():
    """GTK3 windows follow the desktop's light/dark setting."""
    try:
        src = Gio.SettingsSchemaSource.get_default()
        if src and src.lookup('org.gnome.desktop.interface', True) and \
                src.lookup('org.gnome.desktop.interface', True).has_key('color-scheme'):
            gs = Gio.Settings.new('org.gnome.desktop.interface')

            def apply(*_):
                Gtk.Settings.get_default().set_property('gtk-application-prefer-dark-theme',
                                                        gs.get_string('color-scheme') == 'prefer-dark')
            gs.connect('changed::color-scheme', apply)
            apply()
            return gs
    except Exception as e:
        log('dark mode: %s' % e)
    return None


# ------------------------------------------------------------------ small pictures
class Thumbs:
    W, H = 240, 135

    def __init__(self):
        self.mem, self.pending, self.dir = {}, {}, os.path.join(CACHE, 'thumbs')
        os.makedirs(self.dir, exist_ok=True)

    def get(self, it, ready):
        """The picture if we have it; otherwise None now and the real one through ready(pixbuf) later."""
        if it.slug in self.mem:
            return self.mem[it.slug]
        f = os.path.join(self.dir, it.slug + '.jpg')
        if os.path.exists(f):
            try:
                pb = GdkPixbuf.Pixbuf.new_from_file_at_scale(f, self.W, self.H, False)
                self.mem[it.slug] = pb
                return pb
            except GLib.Error:
                pass
        if it.slug in self.pending:
            self.pending[it.slug].append(ready)
        else:
            self.pending[it.slug] = [ready]
            threading.Thread(target=self._fetch, args=(it, f), daemon=True).start()
        return None

    def _fetch(self, it, f):
        try:
            try:
                data = fetch(it.thumb_url())
            except Exception:
                data = fetch(it.urls.get('1920x1080') or next(iter(it.urls.values())), 120)
            loader = GdkPixbuf.PixbufLoader()
            loader.write(data)
            loader.close()
            pb = loader.get_pixbuf().scale_simple(self.W, self.H, GdkPixbuf.InterpType.HYPER)
            pb.savev(f, 'jpeg', ['quality'], ['88'])

            def done():
                self.mem[it.slug] = pb
                for cb in self.pending.pop(it.slug, []):
                    try:
                        cb(pb)
                    except Exception:
                        pass
                return False
            GLib.idle_add(done)
        except Exception as e:
            GLib.idle_add(lambda: (self.pending.pop(it.slug, None), False)[1])
            log('thumbnail %s: %s' % (it.slug, e))


# ------------------------------------------------------------------ the picker window
class Picker(Gtk.Window):
    def __init__(self, app):
        super().__init__(title='Wallpapers')
        self.app = app
        self.set_icon_name(ICON)
        self.set_default_size(1000, 700)
        hb = Gtk.HeaderBar(show_close_button=True, title='Wallpapers', subtitle='from jonsolberg.se')
        self.set_titlebar(hb)
        self.search = Gtk.SearchEntry(placeholder_text='Search')
        self.search.connect('search-changed', lambda *_: self.flow.invalidate_filter())
        hb.pack_end(self.search)
        nxt = Gtk.Button.new_from_icon_name('media-skip-forward-symbolic', Gtk.IconSize.BUTTON)
        nxt.set_tooltip_text('Next wallpaper')
        nxt.connect('clicked', lambda *_: app.next())
        hb.pack_start(nxt)
        self.favbtn = Gtk.ToggleButton(label='★ Favourites only')
        self.favbtn.connect('toggled', lambda *_: self.flow.invalidate_filter())
        hb.pack_start(self.favbtn)

        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=10, margin=14)
        self.top = Gtk.Box(spacing=14, homogeneous=True)
        box.pack_start(self.top, False, False, 0)
        self.hint = Gtk.Label(xalign=0)
        self.hint.get_style_context().add_class('dim-label')
        box.pack_start(self.hint, False, False, 0)
        self.flow = Gtk.FlowBox(homogeneous=True, selection_mode=Gtk.SelectionMode.NONE, column_spacing=10,
                                row_spacing=10, max_children_per_line=8, valign=Gtk.Align.START)
        self.flow.set_filter_func(self._filter)
        sc = Gtk.ScrolledWindow(hscrollbar_policy=Gtk.PolicyType.NEVER)
        sc.add(self.flow)
        box.pack_start(sc, True, True, 0)
        self.add(box)
        self.connect('delete-event', lambda *_: self.hide() or True)
        self.refresh()

    def _filter(self, child):
        it = child.item
        q = self.search.get_text().strip().lower()
        return (not q or q in it.title.lower()) and (not self.favbtn.get_active() or it.slug in self.app.s.favs)

    def card(self, it, caption=None, big=False):
        s = self.app.s
        v = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4)
        img = Gtk.Image()
        w, h = (Thumbs.W, Thumbs.H)
        pb = self.app.thumbs.get(it, lambda p: img.set_from_pixbuf(p))
        if pb:
            img.set_from_pixbuf(pb)
        else:
            img.set_size_request(w, h)
        btn = Gtk.Button(relief=Gtk.ReliefStyle.NONE, tooltip_text='Use “%s”' % it.title)
        btn.add(img)
        btn.connect('clicked', lambda *_: self.app.choose(it))
        if it.slug == s.current:
            btn.get_style_context().add_class('suggested-action')
        v.pack_start(btn, False, False, 0)
        row = Gtk.Box(spacing=4)
        lab = Gtk.Label(xalign=0, ellipsize=Pango.EllipsizeMode.END, max_width_chars=22)
        lab.set_markup(('<small>%s</small>\n' % GLib.markup_escape_text(caption) if caption else '') +
                       ('<b>%s</b>' if it.slug == s.current else '%s') % GLib.markup_escape_text(it.title))
        row.pack_start(lab, True, True, 0)
        star = Gtk.ToggleButton(label='★' if it.slug in s.favs else '☆', relief=Gtk.ReliefStyle.NONE,
                                active=it.slug in s.favs, tooltip_text='Favourite')

        def fav(b):
            self.app.set_fav(it, b.get_active())
            b.set_label('★' if b.get_active() else '☆')
        star.connect('toggled', fav)
        row.pack_end(star, False, False, 0)
        v.pack_start(row, False, False, 0)
        return v

    def refresh(self):
        a = self.app
        for c in self.top.get_children():
            self.top.remove(c)
        cur, prev, up = a.find(a.s.current), a.find(a.s.history[0]) if a.s.history else None, a.upcoming()
        for it, cap in ((prev, 'Previous'), (cur, 'Now'), (up, 'Next')):
            if it:
                self.top.pack_start(self.card(it, cap), False, False, 0)
        for c in self.flow.get_children():
            self.flow.remove(c)
        for it in a.items:
            if it.slug in a.s.hidden:
                continue
            c = Gtk.FlowBoxChild()
            c.item = it
            c.add(self.card(it))
            self.flow.add(c)
        sw, sh = screen_px()
        self.hint.set_text('%d wallpapers · your screen %d×%d gets %s · click one to use it' %
                           (len(a.items), sw, sh, a.size_for_screen().replace('x', '×')))
        self.show_all()


# ------------------------------------------------------------------ the tray app
class App:
    def __init__(self):
        self.s = Settings()
        self.items = cached_list()
        self.list_error = None
        self.last_check = 0
        self._upcoming = None
        self.thumbs = Thumbs()
        self.picker = None
        self.busy = False
        self._dark = follow_dark_mode()
        if not self.s.autostartAsked:
            self.set_autostart(True)
            self.s.autostartAsked = True
            self.s.save()
        if Indicator is None:
            log('no AppIndicator library; opening the window instead')
            self.ind = None
        else:
            self.ind = Indicator.Indicator.new(APP, ICON, Indicator.IndicatorCategory.APPLICATION_STATUS)
            self.ind.set_status(Indicator.IndicatorStatus.ACTIVE)
            self.ind.set_title('Wallpapers')
        self.build()
        GLib.timeout_add_seconds(2, self.first)
        GLib.timeout_add_seconds(60, self.tick)

    # -------------------------------------------------- helpers
    def find(self, slug):
        return next((i for i in self.items if i.slug == slug), None)

    def bg(self, work, done=None, err=None):
        """Run network work off the main thread, then come back to it."""
        def go():
            try:
                r = work()
                GLib.idle_add(lambda: (done(r) if done else None, False)[1])
            except Exception as e:
                log('error: %s' % e)
                GLib.idle_add(lambda: ((err or self.show_error)(e), False)[1])
        threading.Thread(target=go, daemon=True).start()

    def show_error(self, e):
        notify('Wallpapers', 'Could not reach jonsolberg.se (%s)' % e)

    def refresh_list(self):
        try:
            items = load_list()
            self.list_error = None
            return items
        except Exception as e:
            self.list_error = str(e)
            log('list failed: %s' % e)
            return None

    def got_list(self, items):
        if items:
            self.items = items
            if self.s.baseline == 0:
                self.s.baseline = items[0].time
                self.s.save()
        self.build()
        if self.picker and self.picker.get_visible():
            self.picker.refresh()

    def size_for_screen(self, it=None):
        sw, sh = screen_px()
        if self.s.size != 'auto' and (it is None or self.s.size in it.urls):
            return self.s.size
        return best_size(list(it.urls) if it else SIZES, sw, sh)

    # -------------------------------------------------- choosing
    def pick_next(self):
        s = self.s
        pool = [i for i in self.items if i.slug not in s.hidden]
        if s.favOnly and s.favs:
            f = [i for i in pool if i.slug in s.favs]
            pool = f or pool
        if not pool:
            pool = list(self.items)
        if not pool:
            return None
        if s.newFirst and s.baseline:
            for i in pool:
                if i.time > s.baseline and i.slug not in s.ever:
                    return i
        if s.order == 'order':
            at = next((k for k, i in enumerate(pool) if i.slug == s.current), -1)
            return pool[(at + 1) % len(pool)]
        left = [i for i in pool if i.slug not in s.done and i.slug != s.current]
        if not left:
            s.done = []
            left = [i for i in pool if i.slug != s.current] or pool
        return random.choice(left)

    def upcoming(self):
        u = self.find(self._upcoming.slug) if self._upcoming else None
        s = self.s
        if u is None or u.slug == s.current or u.slug in s.hidden or (s.favOnly and s.favs and u.slug not in s.favs):
            u = self.pick_next()
        self._upcoming = u
        return u

    def take_upcoming(self):
        u = self.upcoming()
        self._upcoming = None
        return u

    def apply(self, it, push_history=True, then=None):
        if not it or self.busy:
            return
        self.busy = True
        key = self.size_for_screen(it)
        url = it.urls.get(key) or it.urls.get('1920x1080') or next(iter(it.urls.values()))

        def done(path):
            self.busy = False
            set_wallpaper(path, self.s.target)
            s = self.s
            if push_history and s.current and s.current != it.slug:
                s.history = ([s.current] + [h for h in s.history if h != s.current])[:30]
            s.current = it.slug
            s.lastChange = int(time.time())
            if it.slug not in s.done:
                s.done.append(it.slug)
            if it.slug not in s.ever:
                s.ever.append(it.slug)
            s.save()
            log('wallpaper: %s (%s)' % (it.slug, key))
            self.build()
            if self.picker and self.picker.get_visible():
                self.picker.refresh()
            if then:
                then(it)

        def err(e):
            self.busy = False
            self.show_error(e)
        self.bg(lambda: download(url), done, err)

    # -------------------------------------------------- actions
    def next(self, *_):
        if not self.items:
            self.bg(self.refresh_list, lambda r: (self.got_list(r), self.next()) if r else self.show_error(self.list_error))
            return
        self.apply(self.take_upcoming(), then=lambda it: notify('Wallpapers', 'Wallpaper: ' + it.title))

    def previous(self, *_):
        s = self.s
        if not s.history:
            return
        it = self.find(s.history[0])
        if not it:
            s.history.pop(0)
            s.save()
            return self.previous()
        cur = s.current
        s.history.pop(0)
        self.apply(it, push_history=False)
        self._upcoming = self.find(cur)      # "Next" goes back to the one you came from

    def choose(self, it):
        self.apply(it)

    def set_fav(self, it, on):
        s = self.s
        if on and it.slug not in s.favs:
            s.favs.append(it.slug)
        if not on and it.slug in s.favs:
            s.favs.remove(it.slug)
        s.save()
        self.build()

    def skip(self, *_):
        s = self.s
        if s.current and s.current not in s.hidden:
            s.hidden.append(s.current)
            s.save()
        self.next()

    def save_copy(self, *_):
        it = self.find(self.s.current)
        if not it:
            return
        key = self.size_for_screen(it)

        def done(path):
            pics = GLib.get_user_special_dir(GLib.UserDirectory.DIRECTORY_PICTURES) or os.path.join(HOME, 'Pictures')
            os.makedirs(pics, exist_ok=True)
            dst = os.path.join(pics, os.path.basename(path))
            shutil.copy2(path, dst)
            notify('Saved', dst)
        self.bg(lambda: download(it.urls[key]), done)

    def open_picker(self, *_):
        if not self.picker:
            self.picker = Picker(self)
            if self.ind is None:      # no tray icon available: the window is the app
                self.picker.connect('delete-event', Gtk.main_quit)
        else:
            self.picker.refresh()
        self.picker.present()

    def set_autostart(self, on):
        if on:
            os.makedirs(os.path.dirname(AUTOSTART), exist_ok=True)
            with open(AUTOSTART, 'w') as fh:
                fh.write('[Desktop Entry]\nType=Application\nName=Wallpapers from jonsolberg.se\n'
                         'TryExec=jonsolberg-wallpapers\nExec=jonsolberg-wallpapers --background\nIcon=%s\nX-GNOME-Autostart-enabled=true\n'
                         'X-GNOME-Autostart-Delay=10\nNoDisplay=false\n' % ICON)
        elif os.path.exists(AUTOSTART):
            os.remove(AUTOSTART)

    def set(self, key, value, then=None):
        self.s[key] = value
        if then:
            then()
        self.s.save()
        self._upcoming = None
        self.build()

    # -------------------------------------------------- schedule
    def is_due(self):
        s = self.s
        if s.mode in ('off', 'new'):
            return False
        last = datetime.fromtimestamp(s.lastChange) if s.lastChange else datetime.min
        now = datetime.now()
        if s.mode == 'hours':
            return now - last >= timedelta(hours=s.hours)
        at = now.replace(hour=s.hour, minute=0, second=0, microsecond=0)
        if s.mode == 'daily':
            if now < at:
                at -= timedelta(days=1)
            return last < at
        if s.mode == 'weekly':
            wd = WEEKDAYS.index(s.weekday) if s.weekday in WEEKDAYS else 0
            at -= timedelta(days=(now.weekday() - wd) % 7)
            if at > now:
                at -= timedelta(days=7)
            return last < at
        return False

    def first(self):
        def done(items):
            self.got_list(items)
            if not self.s.current or self.is_due():
                self.apply(self.take_upcoming())
        self.bg(self.refresh_list, done)
        return False

    def tick(self):
        s, due = self.s, self.is_due()
        if not due and s.mode != 'new' and not s.notify:
            return True
        if not due and time.time() - self.last_check < 55 * 60:
            return True
        self.last_check = time.time()

        def done(items):
            self.got_list(items)
            if self.list_error or not self.items:
                return
            newest = self.items[0]
            if s.notify:
                if s.notified == 0:
                    s.notified = newest.time
                    s.save()
                elif newest.time > s.notified:
                    n = sum(1 for i in self.items if i.time > s.notified)
                    notify('New wallpapers', 'New: ' + newest.title if n == 1 else '%d new wallpapers. Newest: %s' % (n, newest.title))
                    s.notified = newest.time
                    s.save()
            if s.mode == 'new':
                if s.followed == 0:
                    s.followed = newest.time
                    s.save()
                elif newest.time > s.followed:
                    s.followed = newest.time
                    s.save()
                    self.apply(newest)
            elif due:
                self.apply(self.take_upcoming())
        self.bg(self.refresh_list, done)
        return True

    # -------------------------------------------------- the menu
    def build(self):
        if self.ind is None:
            return
        s = self.s
        m = Gtk.Menu()

        def item(label, cb=None, sensitive=True, menu=m):
            mi = Gtk.MenuItem(label=label)
            mi.set_sensitive(sensitive)
            if cb:
                mi.connect('activate', lambda *_: cb())
            menu.append(mi)
            return mi

        def check(label, active, cb, menu=m):
            mi = Gtk.CheckMenuItem(label=label)
            mi.set_active(active)
            mi.connect('toggled', lambda w: cb(w.get_active()))
            menu.append(mi)
            return mi

        def radios(menu, choices):
            group = None
            for label, active, cb in choices:
                mi = Gtk.RadioMenuItem.new_with_label_from_widget(group, label)
                group = group or mi
                mi.set_active(active)
                mi.connect('toggled', lambda w, cb=cb: w.get_active() and cb())
                menu.append(mi)

        def sub(label):
            mi = Gtk.MenuItem(label=label)
            sm = Gtk.Menu()
            mi.set_submenu(sm)
            m.append(mi)
            return sm

        cur = self.find(s.current)
        def small(pb):
            return pb.scale_simple(80, 45, GdkPixbuf.InterpType.BILINEAR)

        def pic(label, it, cb=None, sensitive=True, menu=m):
            """A menu line with a small picture of the wallpaper (filled in when it has been downloaded)."""
            if it is None:
                return item(label, cb, sensitive, menu)
            mi = Gtk.ImageMenuItem(label=label)
            mi.set_always_show_image(True)
            mi.set_sensitive(sensitive)
            pb = self.thumbs.get(it, lambda p: mi.set_image(Gtk.Image.new_from_pixbuf(small(p))))
            if pb:
                mi.set_image(Gtk.Image.new_from_pixbuf(small(pb)))
            if cb:
                mi.connect('activate', lambda *_: cb())
            menu.append(mi)
            return mi

        pic(('Now: ' + cur.title) if cur else 'Wallpapers from jonsolberg.se', cur)
        if self.list_error and not self.items:
            item('No connection to jonsolberg.se', sensitive=False)
        up = self.upcoming() if self.items else None
        nxt = pic(('Next: ' + up.title) if up else 'Next wallpaper', up, self.next)
        prev = self.find(s.history[0]) if s.history else None
        pic(('Previous: ' + prev.title) if prev else 'Previous wallpaper', prev, self.previous, bool(prev))
        if self.items:
            ch = sub('Choose a wallpaper')
            for it in [i for i in self.items if i.slug not in s.hidden][:25]:
                label = ('● ' if it.slug == s.current else '') + it.title + ('  ★' if it.slug in s.favs else '') + \
                        ('  (new)' if s.baseline and it.time > s.baseline and it.slug not in s.ever else '')
                pic(label, it, lambda it=it: self.choose(it), menu=ch)
            ch.append(Gtk.SeparatorMenuItem())
            item('All wallpapers, with big pictures…', self.open_picker, menu=ch)
        item('Browse all wallpapers…', self.open_picker)
        m.append(Gtk.SeparatorMenuItem())
        if cur:
            check('★ Favourite', cur.slug in s.favs, lambda on: self.set_fav(cur, on))
            item('Skip this one (never show again)', self.skip)
            item('Save a copy to Pictures', self.save_copy)
            m.append(Gtk.SeparatorMenuItem())

        auto = sub('Change automatically')
        radios(auto, [
            ('Never', s.mode == 'off', lambda: self.set('mode', 'off')),
            ('Every hour', s.mode == 'hours' and s.hours == 1, lambda: self.set('mode', 'hours', lambda: s.update(hours=1))),
            ('Every 3 hours', s.mode == 'hours' and s.hours == 3, lambda: self.set('mode', 'hours', lambda: s.update(hours=3))),
            ('Every 6 hours', s.mode == 'hours' and s.hours == 6, lambda: self.set('mode', 'hours', lambda: s.update(hours=6))),
            ('Every day at %02d:00' % s.hour, s.mode == 'daily', lambda: self.set('mode', 'daily')),
            ('Every %s at %02d:00' % (s.weekday, s.hour), s.mode == 'weekly', lambda: self.set('mode', 'weekly')),
            ('When a new one is published', s.mode == 'new', lambda: self.set('mode', 'new', lambda: s.update(followed=0))),
        ])
        if s.mode in ('daily', 'weekly'):
            auto.append(Gtk.SeparatorMenuItem())
            hm = Gtk.MenuItem(label='Time of day')
            hsub = Gtk.Menu()
            hm.set_submenu(hsub)
            radios(hsub, [('%02d:00' % h, s.hour == h, lambda h=h: self.set('hour', h)) for h in (0, 6, 7, 8, 9, 12, 18, 21)])
            auto.append(hm)
            if s.mode == 'weekly':
                wm = Gtk.MenuItem(label='Day of the week')
                wsub = Gtk.Menu()
                wm.set_submenu(wsub)
                radios(wsub, [(d, s.weekday == d, lambda d=d: self.set('weekday', d)) for d in WEEKDAYS])
                auto.append(wm)

        on = sub('Use it on')
        radios(on, [('Desktop', s.target == 'desktop', lambda: self.set('target', 'desktop')),
                    ('Lock screen', s.target == 'lock', lambda: self.set('target', 'lock')),
                    ('Desktop and lock screen', s.target == 'both', lambda: self.set('target', 'both'))])

        order = sub('Which one next')
        radios(order, [('Random (no repeats until all are shown)', s.order == 'random', lambda: self.set('order', 'random')),
                       ('In order, newest first', s.order == 'order', lambda: self.set('order', 'order'))])
        order.append(Gtk.SeparatorMenuItem())
        check('New wallpapers first', s.newFirst, lambda v: self.set('newFirst', v), order)
        check('Only favourites (%d)' % len(s.favs), s.favOnly, lambda v: self.set('favOnly', v), order)
        if s.hidden:
            item('Bring back skipped ones (%d)' % len(s.hidden), lambda: self.set('hidden', []), menu=order)

        sz = sub('Picture size')
        sw, sh = screen_px()
        radios(sz, [('Automatic (this screen %d×%d gets %s)' % (sw, sh, best_size(SIZES, sw, sh).replace('x', '×')),
                     s.size == 'auto', lambda: self.set('size', 'auto'))] +
               [(SIZE_NAMES[k], s.size == k, lambda k=k: self.set('size', k)) for k in SIZES])

        check('Tell me about new wallpapers', s.notify, lambda v: self.set('notify', v, lambda: s.update(notified=0)))
        check('Start when I log in', os.path.exists(AUTOSTART), lambda v: (self.set_autostart(v), self.build()))
        m.append(Gtk.SeparatorMenuItem())
        item('Open the gallery on jonsolberg.se', lambda: Gio.AppInfo.launch_default_for_uri(BASE + '/Pictures/', None))
        item('❤️ Support me on Ko-fi', lambda: Gio.AppInfo.launch_default_for_uri('https://ko-fi.com/jonsolberg', None))
        item('Version %s · jonsolberg.se' % VERSION, lambda: Gio.AppInfo.launch_default_for_uri(BASE + '/', None))
        item('Quit', Gtk.main_quit)
        m.show_all()
        self.ind.set_secondary_activate_target(None)
        self.ind.set_menu(m)
        self.ind.set_secondary_activate_target(nxt)   # middle-click the icon = next wallpaper
        self.menu = m


def main():
    lock = open(os.path.join(CACHE, 'running.lock'), 'w')
    try:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except OSError:
        # already running: ask it to show the window instead
        open(os.path.join(CACHE, 'show-window'), 'w').close()
        return
    app = App()
    # started from the app menu: show the window; started at login: only the icon in the top bar
    if Indicator is None or '--background' not in sys.argv:
        GLib.idle_add(lambda: (app.open_picker(), False)[1])
    flag = os.path.join(CACHE, 'show-window')

    def poll():
        if os.path.exists(flag):
            os.remove(flag)
            app.open_picker()
        return True
    GLib.timeout_add(700, poll)
    Gtk.main()


if __name__ == '__main__':
    if '--uninstall-autostart' in sys.argv:
        if os.path.exists(AUTOSTART):
            os.remove(AUTOSTART)
        sys.exit(0)
    main()
