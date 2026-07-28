use slint::ComponentHandle;
use xhm_desktop::OrganicAnimationSpike;

fn main() -> Result<(), slint::PlatformError> {
    if std::env::var_os("SLINT_BACKEND").is_none() {
        std::env::set_var("SLINT_BACKEND", "winit-software");
    }
    let backend = std::env::var("SLINT_BACKEND").unwrap_or_else(|_| "winit-software".into());
    println!("ORGANIC_SPIKE backend={backend} states=6 timelines=3");
    let window = OrganicAnimationSpike::new()?;
    window.run()
}
