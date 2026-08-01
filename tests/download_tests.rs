//! 下载链路集成测试（wiremock 模拟 Mojang API）
//!
//! 覆盖：库下载（SHA1 校验/失败重下/已存在跳过）、native 筛选与解压、
//! 旧版 URL 回退、资源下载（注入 base_url + SHA1 失败）、版本清单/详情获取、
//! 客户端 jar 下载。

use furry_minecraft::download::{
    fetch_version_details, fetch_version_manifest_from, AssetDownloader, DownloadEngine,
    LibraryDownloader,
};
use furry_minecraft::version::manifest::{
    AssetIndex, AssetObject, DownloadInfo, Downloads, ExtractConfig, Library, LibraryArtifact,
    LibraryDownloads, VersionDetails,
};
use furry_minecraft::version_manager::VersionInstaller;
use sha1::Digest;
use std::collections::HashMap;
use std::io::Write;
use wiremock::{matchers, Mock, MockServer, ResponseTemplate};

/// 计算 SHA1（十六进制小写）
fn sha1_hex(data: &[u8]) -> String {
    format!("{:x}", sha1::Sha1::digest(data))
}

/// 构造内存 zip（条目名 → 内容）
fn make_zip(entries: &[(&str, &[u8])]) -> Vec<u8> {
    let mut buf = std::io::Cursor::new(Vec::new());
    {
        let mut zip = zip::ZipWriter::new(&mut buf);
        let opts = zip::write::SimpleFileOptions::default()
            .compression_method(zip::CompressionMethod::Stored);
        for (name, data) in entries {
            zip.start_file(*name, opts).unwrap();
            zip.write_all(data).unwrap();
        }
        zip.finish().unwrap();
    }
    buf.into_inner()
}

// ============================================================
// 库文件下载
// ============================================================

/// 基础库下载：成功写入 + SHA1 校验通过 + 已存在跳过（不重复请求）
#[tokio::test]
async fn test_library_download_success_and_skip() {
    let server = MockServer::start().await;
    let content = b"fake library jar content".to_vec();
    let sha1 = sha1_hex(&content);
    let rel = "com/example/test/1.0/test-1.0.jar";

    Mock::given(matchers::method("GET"))
        .and(matchers::path(format!("/{}", rel)))
        .respond_with(ResponseTemplate::new(200).set_body_bytes(content.clone()))
        .mount(&server)
        .await;

    let lib = Library {
        name: "com.example:test:1.0".into(),
        downloads: Some(LibraryDownloads {
            artifact: Some(LibraryArtifact {
                path: rel.into(),
                sha1,
                size: content.len() as u64,
                url: format!("{}/{}", server.uri(), rel),
            }),
            classifiers: None,
        }),
        url: None,
        rules: None,
        natives: None,
        extract: None,
    };

    let dir = tempfile::tempdir().unwrap();
    let downloader = LibraryDownloader::new(dir.path());
    let entries = downloader
        .download_libraries(std::slice::from_ref(&lib))
        .await
        .unwrap();
    assert_eq!(entries.len(), 1);

    let dest = dir.path().join(rel);
    assert!(dest.exists(), "库文件应被写入");
    assert_eq!(sha1_hex(&std::fs::read(&dest).unwrap()), sha1_hex(&content));

    // 二次下载：已存在且校验通过 → 不发起请求
    let n_before = server.received_requests().await.unwrap().len();
    downloader.download_libraries(&[lib]).await.unwrap();
    let n_after = server.received_requests().await.unwrap().len();
    assert_eq!(n_after, n_before, "已存在且校验通过的库不应重复下载");
}

/// SHA1 内容不匹配 → 下载报 ValidationError
#[tokio::test]
async fn test_library_sha1_mismatch_error() {
    let server = MockServer::start().await;
    let rel = "com/example/test/1.0/test-1.0.jar";

    Mock::given(matchers::method("GET"))
        .and(matchers::path(format!("/{}", rel)))
        .respond_with(ResponseTemplate::new(200).set_body_bytes(b"corrupted content"))
        .mount(&server)
        .await;

    let lib = Library {
        name: "com.example:test:1.0".into(),
        downloads: Some(LibraryDownloads {
            artifact: Some(LibraryArtifact {
                path: rel.into(),
                // 声明与实际内容不符的 SHA1
                sha1: "0".repeat(40),
                size: 100,
                url: format!("{}/{}", server.uri(), rel),
            }),
            classifiers: None,
        }),
        url: None,
        rules: None,
        natives: None,
        extract: None,
    };

    let dir = tempfile::tempdir().unwrap();
    let result = LibraryDownloader::new(dir.path())
        .download_libraries(&[lib])
        .await;
    assert!(result.is_err(), "SHA1 不匹配时应返回校验错误");
    assert!(
        matches!(
            result.err(),
            Some(furry_minecraft::LauncherError::ValidationError { .. })
        ),
        "应为 ValidationError"
    );
}

/// 已存在但校验失败的坏文件 → 重新下载覆盖
#[tokio::test]
async fn test_library_bad_file_replaced() {
    let server = MockServer::start().await;
    let content = b"correct library content".to_vec();
    let sha1 = sha1_hex(&content);
    let rel = "com/example/test/1.0/test-1.0.jar";

    Mock::given(matchers::method("GET"))
        .and(matchers::path(format!("/{}", rel)))
        .respond_with(ResponseTemplate::new(200).set_body_bytes(content.clone()))
        .mount(&server)
        .await;

    let lib = Library {
        name: "com.example:test:1.0".into(),
        downloads: Some(LibraryDownloads {
            artifact: Some(LibraryArtifact {
                path: rel.into(),
                sha1,
                size: content.len() as u64,
                url: format!("{}/{}", server.uri(), rel),
            }),
            classifiers: None,
        }),
        url: None,
        rules: None,
        natives: None,
        extract: None,
    };

    let dir = tempfile::tempdir().unwrap();
    // 预置一个内容错误（SHA1 不匹配）的文件
    let dest = dir.path().join(rel);
    std::fs::create_dir_all(dest.parent().unwrap()).unwrap();
    std::fs::write(&dest, b"stale corrupted file").unwrap();

    LibraryDownloader::new(dir.path())
        .download_libraries(&[lib])
        .await
        .unwrap();

    // 旧坏文件应被正确内容覆盖
    assert_eq!(
        sha1_hex(&std::fs::read(&dest).unwrap()),
        sha1_hex(&content),
        "校验失败的坏文件应被重新下载覆盖"
    );
}

/// native 库：os 筛选 + 下载 + 解压（排除 META-INF 与 exclude 项）
#[tokio::test]
async fn test_library_native_filter_and_extract() {
    let server = MockServer::start().await;

    // 构造 native zip：一个有效库 + 一个 META-INF 条目
    let zip_data = make_zip(&[
        ("libtest_native.so", b"native binary".as_slice()),
        ("META-INF/MANIFEST.MF", b"Manifest-Version: 1.0".as_slice()),
        ("README.txt", b"excluded file".as_slice()),
    ]);

    let win_path = "com/example/native/1.0/native-1.0-natives-windows.jar";
    Mock::given(matchers::method("GET"))
        .and(matchers::path(format!("/{}", win_path)))
        .respond_with(ResponseTemplate::new(200).set_body_bytes(zip_data.clone()))
        .mount(&server)
        .await;

    let mut classifiers = HashMap::new();
    classifiers.insert(
        "natives-windows".to_string(),
        LibraryArtifact {
            path: win_path.into(),
            sha1: String::new(),
            size: zip_data.len() as u64,
            url: format!("{}/{}", server.uri(), win_path),
        },
    );

    let mut natives = HashMap::new();
    natives.insert("windows".to_string(), "natives-windows".to_string());
    natives.insert("linux".to_string(), "natives-linux".to_string());

    let lib = Library {
        name: "com.example:native:1.0".into(),
        downloads: Some(LibraryDownloads {
            artifact: None,
            classifiers: Some(classifiers),
        }),
        url: None,
        rules: None,
        natives: Some(natives),
        extract: Some(ExtractConfig {
            exclude: vec!["README.txt".into()],
        }),
    };

    let dir = tempfile::tempdir().unwrap();
    let natives_dir = dir.path().join("natives");
    LibraryDownloader::new(dir.path())
        .natives_dir(&natives_dir)
        .os_name("windows")
        .download_libraries(&[lib])
        .await
        .unwrap();

    // 有效 native 被解压
    assert!(
        natives_dir.join("libtest_native.so").exists(),
        "native 库应被解压"
    );
    // META-INF 被硬编码排除
    assert!(!natives_dir.join("META-INF").exists(), "META-INF 应被排除");
    // exclude 列表中的条目被排除
    assert!(
        !natives_dir.join("README.txt").exists(),
        "exclude 条目应被排除"
    );
    // 不应请求 linux 的 classifier
    let req_paths: Vec<String> = server
        .received_requests()
        .await
        .unwrap()
        .iter()
        .map(|r| r.url.path().to_string())
        .collect();
    assert!(
        !req_paths.iter().any(|p| p.contains("natives-linux")),
        "应只下载当前 OS 的 native（windows）"
    );
}

/// 旧版库（无 downloads，仅 url）→ Maven 路径回退下载
#[tokio::test]
async fn test_library_legacy_url_fallback() {
    let server = MockServer::start().await;
    let content = b"legacy library".to_vec();
    let rel = "com/example/legacy/1.0/legacy-1.0.jar";

    Mock::given(matchers::method("GET"))
        .and(matchers::path(format!("/{}", rel)))
        .respond_with(ResponseTemplate::new(200).set_body_bytes(content.clone()))
        .mount(&server)
        .await;

    let lib = Library {
        name: "com.example:legacy:1.0".into(),
        downloads: None,
        // 旧版格式：仅提供仓库根 URL
        url: Some(server.uri()),
        rules: None,
        natives: None,
        extract: None,
    };

    let dir = tempfile::tempdir().unwrap();
    LibraryDownloader::new(dir.path())
        .download_libraries(&[lib])
        .await
        .unwrap();

    assert!(dir.path().join(rel).exists(), "旧版 URL 回退应下载成功");
}

// ============================================================
// 资源文件下载
// ============================================================

/// 资源下载：SHA1 校验 + 写入 objects/{2}/{hash}
#[tokio::test]
async fn test_asset_download() {
    let server = MockServer::start().await;
    let content = b"asset content bytes".to_vec();
    let hash = sha1_hex(&content);

    Mock::given(matchers::method("GET"))
        .and(matchers::path(format!("/{}/{}", &hash[..2], hash)))
        .respond_with(ResponseTemplate::new(200).set_body_bytes(content.clone()))
        .mount(&server)
        .await;

    let mut objects = HashMap::new();
    objects.insert(
        "icons/icon_16x16.png".to_string(),
        AssetObject {
            hash: hash.clone(),
            size: content.len() as u64,
        },
    );
    let index = AssetIndex { objects };

    let dir = tempfile::tempdir().unwrap();
    AssetDownloader::new(dir.path().join("assets"))
        .with_base_url(server.uri())
        .download_assets(&index)
        .await
        .unwrap();

    let dest = dir
        .path()
        .join("assets")
        .join("objects")
        .join(&hash[..2])
        .join(&hash);
    assert!(dest.exists(), "资源文件应被写入");
    assert_eq!(
        sha1_hex(&std::fs::read(&dest).unwrap()),
        hash,
        "资源文件内容应与 hash 一致"
    );
}

/// 资源 SHA1 不匹配 → ValidationError
#[tokio::test]
async fn test_asset_sha1_mismatch_error() {
    let server = MockServer::start().await;
    let hash = sha1_hex(b"expected content");

    Mock::given(matchers::method("GET"))
        .and(matchers::path(format!("/{}/{}", &hash[..2], hash)))
        .respond_with(ResponseTemplate::new(200).set_body_bytes(b"different content"))
        .mount(&server)
        .await;

    let mut objects = HashMap::new();
    objects.insert(
        "some/asset.bin".to_string(),
        AssetObject {
            hash: hash.clone(),
            size: 100,
        },
    );
    let index = AssetIndex { objects };

    let dir = tempfile::tempdir().unwrap();
    let result = AssetDownloader::new(dir.path().join("assets"))
        .with_base_url(server.uri())
        .download_assets(&index)
        .await;
    assert!(result.is_err(), "资源 SHA1 不匹配应报错");
}

// ============================================================
// 版本清单 / 详情获取
// ============================================================

/// 版本清单获取（注入 mock 地址）
#[tokio::test]
async fn test_fetch_version_manifest_from_mock() {
    let server = MockServer::start().await;
    let manifest_json = serde_json::json!({
        "latest": { "release": "1.21", "snapshot": "24w01a" },
        "versions": [
            {
                "id": "1.21",
                "type": "release",
                "url": "https://example.com/1.21.json",
                "time": "2024-06-12T14:21:00+00:00",
                "releaseTime": "2024-06-12T14:21:00+00:00",
                "sha1": "abc123",
                "complianceLevel": 1
            }
        ]
    });

    Mock::given(matchers::method("GET"))
        .and(matchers::path("/manifest.json"))
        .respond_with(ResponseTemplate::new(200).set_body_json(manifest_json))
        .mount(&server)
        .await;

    let manifest = fetch_version_manifest_from(&format!("{}/manifest.json", server.uri()))
        .await
        .unwrap();
    assert_eq!(manifest.latest.release, "1.21");
    assert_eq!(manifest.versions.len(), 1);
    assert_eq!(manifest.versions[0].id, "1.21");
}

/// 版本详情获取
#[tokio::test]
async fn test_fetch_version_details_mock() {
    let server = MockServer::start().await;
    let details_json = serde_json::json!({
        "id": "1.21",
        "mainClass": "net.minecraft.client.main.Main",
        "minimumLauncherVersion": 21,
        "releaseTime": "2024-06-12T14:21:00+00:00",
        "time": "2024-06-12T14:21:00+00:00",
        "type": "release",
        "libraries": [],
        "downloads": {
            "client": {
                "sha1": "aaa",
                "size": 123,
                "url": "https://example.com/client.jar"
            }
        }
    });

    Mock::given(matchers::method("GET"))
        .and(matchers::path("/v1.21.json"))
        .respond_with(ResponseTemplate::new(200).set_body_json(details_json))
        .mount(&server)
        .await;

    let details = fetch_version_details(&format!("{}/v1.21.json", server.uri()))
        .await
        .unwrap();
    assert_eq!(details.id, "1.21");
    assert!(details.downloads.client.is_some());
}

// ============================================================
// 客户端 jar 下载
// ============================================================

/// 客户端 jar：下载 + SHA1 校验 + 已存在跳过
#[tokio::test]
async fn test_client_jar_download() {
    let server = MockServer::start().await;
    let content = b"fake minecraft client jar".to_vec();
    let sha1 = sha1_hex(&content);

    Mock::given(matchers::method("GET"))
        .and(matchers::path("/client.jar"))
        .respond_with(ResponseTemplate::new(200).set_body_bytes(content.clone()))
        .mount(&server)
        .await;

    let details = VersionDetails {
        asset_index: None,
        assets: None,
        downloads: Downloads {
            client: Some(DownloadInfo {
                sha1,
                size: content.len() as u64,
                url: format!("{}/client.jar", server.uri()),
                path: None,
            }),
            client_mappings: None,
            server: None,
            server_mappings: None,
        },
        id: "1.21".into(),
        libraries: Vec::new(),
        logging: Default::default(),
        main_class: "net.minecraft.client.main.Main".into(),
        minecraft_arguments: None,
        minimum_launcher_version: 21,
        release_time: "2024-06-12T14:21:00+00:00".into(),
        time: "2024-06-12T14:21:00+00:00".into(),
        version_type: furry_minecraft::version::manifest::VersionType::Release,
        arguments: None,
        java_version: None,
    };

    let dir = tempfile::tempdir().unwrap();
    let installer = VersionInstaller::new(DownloadEngine::new(dir.path()));

    let jar_path = installer.install_client_jar(&details).await.unwrap();
    assert!(jar_path.exists(), "客户端 jar 应被写入");
    assert_eq!(
        sha1_hex(&std::fs::read(&jar_path).unwrap()),
        sha1_hex(&content)
    );

    // 已存在且校验通过 → 不重复请求
    let n_before = server.received_requests().await.unwrap().len();
    installer.install_client_jar(&details).await.unwrap();
    let n_after = server.received_requests().await.unwrap().len();
    assert_eq!(n_after, n_before, "已存在的客户端 jar 不应重复下载");
}

/// 客户端 jar 缺少 downloads.client 信息 → 明确错误
#[tokio::test]
async fn test_client_jar_missing_download_info() {
    let details = VersionDetails {
        asset_index: None,
        assets: None,
        downloads: Downloads::default(),
        id: "1.21".into(),
        libraries: Vec::new(),
        logging: Default::default(),
        main_class: "net.minecraft.client.main.Main".into(),
        minecraft_arguments: None,
        minimum_launcher_version: 21,
        release_time: "2024-06-12T14:21:00+00:00".into(),
        time: "2024-06-12T14:21:00+00:00".into(),
        version_type: furry_minecraft::version::manifest::VersionType::Release,
        arguments: None,
        java_version: None,
    };

    let dir = tempfile::tempdir().unwrap();
    let installer = VersionInstaller::new(DownloadEngine::new(dir.path()));
    assert!(installer.install_client_jar(&details).await.is_err());
}
