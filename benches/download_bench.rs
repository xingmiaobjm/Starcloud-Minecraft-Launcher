use criterion::{black_box, criterion_group, criterion_main, Criterion};
use furry_minecraft::version_manager::VersionComparator;

fn bench_version_compare(c: &mut Criterion) {
    c.bench_function("version_compare", |b| {
        b.iter(|| {
            VersionComparator::compare(black_box("1.20.1"), black_box("1.21"));
        })
    });

    c.bench_function("version_diff", |b| {
        b.iter(|| {
            VersionComparator::diff(black_box("1.20"), black_box("1.21"));
        })
    });

    c.bench_function("is_snapshot", |b| {
        b.iter(|| {
            VersionComparator::is_snapshot(black_box("24w01a"));
        })
    });
}

criterion_group!(benches, bench_version_compare);
criterion_main!(benches);
