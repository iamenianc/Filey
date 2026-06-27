# Filey Project Formatting & Architecture Rules

## XAML Style & Structure
- **One Attribute Per Line:** Place each XML attribute on its own line for complex controls.
- **Resource Dictionaries:** Do not define large styles inline or locally in views; put them in standard ResourceDictionary files (e.g., `Themes/Styles.xaml`).
- **Grid Layouts:** Use Grids with star (*) and Auto sizing instead of absolute coordinates or Canvas.
- **Ordering:** Order XML attributes consistently: identity (x:Name), layout, alignment, data binding, then events.

## Component Reusability
- **UserControl Extraction:** Keep views small (under 300 lines). Sub-layouts must be extracted into UserControls.
- **Dependency Properties:** Expose custom properties on UserControls using standard `DependencyProperty.Register` signatures.
