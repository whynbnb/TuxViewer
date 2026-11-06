import Gio from 'gi://Gio';
import Meta from 'gi://Meta';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';

const OBJECT_PATH = '/org/gnome/Shell/Extensions/TuxViewer';
const IFACE_XML = `
<node>
  <interface name="org.gnome.Shell.Extensions.TuxViewer">
    <method name="ListWindows">
      <arg type="s" direction="out"/>
    </method>
    <method name="PickWindowAtCursor">
      <arg type="s" direction="out"/>
    </method>
    <method name="FocusWindow">
      <arg type="t" direction="in"/>
      <arg type="b" direction="out"/>
    </method>
    <method name="CloseWindow">
      <arg type="t" direction="in"/>
      <arg type="b" direction="out"/>
    </method>
  </interface>
</node>`;

export default class TuxViewerExtension extends Extension {
    enable() {
        this._exported = Gio.DBusExportedObject.wrapJSObject(IFACE_XML, this);
        this._exported.export(Gio.DBus.session, OBJECT_PATH);
    }

    disable() {
        if (this._exported) {
            this._exported.unexport();
            this._exported = null;
        }
    }

    ListWindows() {
        return JSON.stringify(this._windows().map(w => this._info(w)));
    }

    PickWindowAtCursor() {
        const [x, y] = global.get_pointer();
        const window = this._windowAt(x, y);
        return window ? JSON.stringify(this._info(window)) : '';
    }

    FocusWindow(id) {
        const window = this._find(id);
        if (!window)
            return false;
        Main.activateWindow(window);
        return true;
    }

    CloseWindow(id) {
        const window = this._find(id);
        if (!window)
            return false;
        window.delete(global.get_current_time());
        return true;
    }

    _windows() {
        const result = [];
        for (const actor of global.get_window_actors()) {
            const window = actor.meta_window;
            if (!window)
                continue;
            if (window.get_window_type() === Meta.WindowType.DESKTOP ||
                window.get_window_type() === Meta.WindowType.DOCK)
                continue;
            if (window.is_skip_taskbar())
                continue;
            result.push(window);
        }
        return result;
    }

    _find(id) {
        const target = Number(id);
        return this._windows().find(w => Number(w.get_id()) === target) ?? null;
    }

    _windowAt(x, y) {
        const windows = global.display.sort_windows_by_stacking(this._windows());
        for (let i = windows.length - 1; i >= 0; i--) {
            const window = windows[i];
            if (window.minimized)
                continue;
            const rect = window.get_frame_rect();
            if (x >= rect.x && x < rect.x + rect.width && y >= rect.y && y < rect.y + rect.height)
                return window;
        }
        return null;
    }

    _info(window) {
        const rect = window.get_frame_rect();
        const workspace = window.get_workspace();
        const clientType = window.get_client_type();
        return {
            id: Number(window.get_id()),
            title: window.get_title() ?? '',
            app_id: window.get_wm_class_instance() ?? '',
            wm_class: window.get_wm_class() ?? '',
            pid: window.get_pid() ?? 0,
            client_type: clientType === Meta.WindowClientType.X11 ? 1 : 0,
            x: rect.x,
            y: rect.y,
            width: rect.width,
            height: rect.height,
            focused: window.has_focus(),
            hidden: window.minimized,
            monitor: window.get_monitor(),
            workspace: workspace ? workspace.index() : -1,
            workspace_name: workspace ? String(workspace.index()) : '',
        };
    }
}
