// G1 编译唯一的 Slint 根。POC 模式：slint_build::compile，不引入自定义代码生成配置。
// 对齐 poc/slint-desktop/build.rs:1-3。
fn main() {
    slint_build::compile("ui/shell.slint").expect("slint shell.slint compile failed");
}
