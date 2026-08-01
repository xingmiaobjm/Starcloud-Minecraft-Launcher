use criterion::{black_box, criterion_group, criterion_main, Criterion};
use furry_minecraft::version_manager::VersionValidator;

fn bench_launch_config_validation(c: &mut Criterion) {
    let config = furry_minecraft::LaunchConfig::basic("1.21", "/tmp/.minecraft", "Player");

    c.bench_function("validate_launch_config", |b| {
        b.iter(|| {
            VersionValidator::validate_launch_config(black_box(&config))
        })
    });

    c.bench_function("validate_url_ok", |b| {
        b.iter(|| {
            VersionValidator::validate_url(black_box("https://example.com"))
        })
    });

    c.bench_function("validate_url_bad", |b| {
        b.iter(|| {
            let _ = VersionValidator::validate_url(black_box("ftp://bad.com"));
        })
    });
}

criterion_group!(benches, bench_launch_config_validation);
criterion_main!(benches);
