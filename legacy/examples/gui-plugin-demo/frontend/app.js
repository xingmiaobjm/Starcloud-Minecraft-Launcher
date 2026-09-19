// 示例视图插件脚本：演示通过 Tauri IPC 调用后端命令
(function () {
  "use strict";

  // 优先使用插件 SDK 的 invokeSafe，回退到 Tauri 全局 invoke
  function invoke(cmd, args) {
    const win = window;
    const raw = win.__TAURI_INTERNALS__ && win.__TAURI_INTERNALS__.invoke;
    if (typeof raw === "function") {
      return raw(cmd, args);
    }
    return Promise.reject(new Error("当前环境无 Tauri invoke（请以 tauri dev/build 运行）"));
  }

  async function load() {
    try {
      const versions = await invoke("list_installed");
      const ul = document.getElementById("versions");
      ul.innerHTML = "";
      if (!versions || versions.length === 0) {
        ul.innerHTML = '<li class="muted">（暂无已安装版本）</li>';
        return;
      }
      versions.forEach(function (v) {
        const li = document.createElement("li");
        li.textContent =
          v.id + " · " + (v.size_bytes / 1024 / 1024).toFixed(1) + " MB";
        ul.appendChild(li);
      });
    } catch (e) {
      document.getElementById("versions").innerHTML =
        '<li class="muted">读取失败：' + e + "</li>";
    }

    try {
      const settings = await invoke("get_settings");
      document.getElementById("settings").textContent = JSON.stringify(
        settings,
        null,
        2
      );
    } catch (e) {
      document.getElementById("settings").textContent = "读取失败：" + e;
    }
  }

  load();
})();
