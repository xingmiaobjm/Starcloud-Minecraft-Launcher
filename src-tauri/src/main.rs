// StarCloud Minecraft Launcher - LGPL-3.0-only
// 启动器入口点

#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

fn main() {
    starcloud_launcher_lib::run()
}
