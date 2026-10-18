// P2P 联机命令 - 基于 EasyTier 陶瓦联机
// StarCloud Minecraft Launcher - LGPL-3.0-only

use serde::{Deserialize, Serialize};
use std::sync::Mutex;

pub struct P2PState {
    pub running: Mutex<bool>,
    pub network_name: Mutex<Option<String>>,
    pub virtual_ip: Mutex<Option<String>>,
}

#[derive(Serialize, Deserialize, Clone)]
pub struct P2PStatus {
    pub connected: bool,
    pub virtual_ip: Option<String>,
    pub network_name: Option<String>,
    pub peers: Vec<PeerInfo>,
}

#[derive(Serialize, Deserialize, Clone)]
pub struct PeerInfo {
    pub ip: String,
    pub hostname: String,
    pub latency: u64,
    pub status: String,
}

#[derive(Serialize, Deserialize)]
pub struct InviteCode {
    pub code: String,
    pub network_name: String,
}

// ========= 命令 =========

/// 创建 P2P 网络
#[tauri::command]
pub async fn p2p_create_network(
    network_name: String,
    p2p_state: tauri::State<'_, P2PState>,
) -> Result<P2PStatus, String> {
    *p2p_state.network_name.lock().unwrap() = Some(network_name.clone());
    *p2p_state.virtual_ip.lock().unwrap() = Some("10.144.144.1".to_string());
    *p2p_state.running.lock().unwrap() = true;

    Ok(P2PStatus {
        connected: true,
        virtual_ip: Some("10.144.144.1".to_string()),
        network_name: Some(network_name),
        peers: vec![],
    })
}

/// 加入 P2P 网络（通过邀请码）
#[tauri::command]
pub async fn p2p_join_network(
    invite_code: String,
    p2p_state: tauri::State<'_, P2PState>,
) -> Result<P2PStatus, String> {
    // 解析邀请码（格式: network_name:virtual_ip）
    let parts: Vec<&str> = invite_code.split(':').collect();
    let network_name = parts.first().unwrap_or(&"unknown").to_string();

    *p2p_state.network_name.lock().unwrap() = Some(network_name.clone());
    *p2p_state.virtual_ip.lock().unwrap() = Some("10.144.144.2".to_string());
    *p2p_state.running.lock().unwrap() = true;

    Ok(P2PStatus {
        connected: true,
        virtual_ip: Some("10.144.144.2".to_string()),
        network_name: Some(network_name),
        peers: vec![],
    })
}

/// 发现 P2P 网络中的其他节点
#[tauri::command]
pub async fn p2p_discover_peers(
    p2p_state: tauri::State<'_, P2PState>,
) -> Result<Vec<PeerInfo>, String> {
    if !*p2p_state.running.lock().unwrap() {
        return Err("P2P 网络未启动".to_string());
    }

    // 占位：返回空列表，后续集成 EasyTier 实现
    Ok(vec![])
}

/// 扫描 P2P 网络中的 Minecraft 服务器
#[tauri::command]
pub async fn p2p_discover_servers(
    p2p_state: tauri::State<'_, P2PState>,
) -> Result<Vec<String>, String> {
    if !*p2p_state.running.lock().unwrap() {
        return Err("P2P 网络未启动".to_string());
    }

    // 占位：扫描 10.144.144.0/24 网段的 25565 端口
    Ok(vec![])
}

/// 获取 P2P 网络状态
#[tauri::command]
pub fn p2p_get_status(
    p2p_state: tauri::State<'_, P2PState>,
) -> Result<P2PStatus, String> {
    let running = *p2p_state.running.lock().unwrap();
    let network_name = p2p_state.network_name.lock().unwrap().clone();
    let virtual_ip = p2p_state.virtual_ip.lock().unwrap().clone();

    Ok(P2PStatus {
        connected: running,
        virtual_ip,
        network_name,
        peers: vec![],
    })
}

/// 离开 P2P 网络
#[tauri::command]
pub fn p2p_leave_network(
    p2p_state: tauri::State<'_, P2PState>,
) -> Result<bool, String> {
    *p2p_state.running.lock().unwrap() = false;
    *p2p_state.network_name.lock().unwrap() = None;
    *p2p_state.virtual_ip.lock().unwrap() = None;
    Ok(true)
}

/// 生成邀请码
#[tauri::command]
pub fn p2p_generate_invite(
    p2p_state: tauri::State<'_, P2PState>,
) -> Result<InviteCode, String> {
    let network_name = p2p_state
        .network_name
        .lock()
        .unwrap()
        .clone()
        .ok_or("网络未创建")?;

    let virtual_ip = p2p_state
        .virtual_ip
        .lock()
        .unwrap()
        .clone()
        .unwrap_or_else(|| "10.144.144.1".to_string());

    let code = format!("{}:{}", network_name, virtual_ip);

    Ok(InviteCode {
        code,
        network_name,
    })
}
