# GIF Utils — UI Design System

**Direction:** Classic Windows desktop utility  
**Scope:** Visual styling only; preserve the current application structure and workflows.

## Visual language

- Use a light Windows utility appearance: flat light-gray window chrome, white input fields, thin square borders, dense spacing, and Segoe UI / Microsoft YaHei UI.
- Prefer native-looking controls over cards, gradients, shadows, oversized typography, or decorative animation.
- Keep corners square (`0px`) and align borders to device pixels.
- Use color sparingly. Blue is reserved for focus, selection, progress, and the default action outline.

## Color tokens

| Role | Value |
|---|---|
| Window background | `#F0F0F0` |
| Section surface | `#F7F7F7` |
| Raised control | `#E1E1E1` |
| Input surface | `#FFFFFF` |
| Primary text | `#111111` |
| Secondary text | `#555555` |
| Border | `#C7C7C7` |
| Strong border | `#8A8A8A` |
| Focus / progress | `#005FB8` / `#0078D4` |
| Hover | `#E5F1FB` |
| Pressed | `#CCE4F7` |
| Error | `#C42B1C` |
| Tooltip | `#FFFFE1` |

## Component rules

- Inputs and combo boxes: 26px high, white background, 1px strong-gray border, blue focus border.
- Buttons: 26px minimum height, gray fill, square outline; blue-tinted hover and pressed states. Center button content horizontally and vertically; controls inside multi-row status strips must also be centered across the full strip. The primary action uses a blue outline instead of a filled promotional color.
- Tabs: compact square tabs with a light-gray inactive state and white selected state.
- Sections: 1px neutral border, no shadow, no rounded corners, 8px padding and 8px vertical separation.
- Tooltips: classic pale-yellow surface, black text, 1px border.
- Scrollbars: light track with a gray square thumb; no decorative arrows.
- Progress bars: white track, gray outline, blue indicator.

## Accessibility and behavior

- Preserve visible keyboard focus and logical tab navigation.
- Preserve AutomationProperties labels and system high-contrast support.
- Disabled controls remain semantically disabled and visibly muted.
- Mouse-wheel input over text boxes and closed combo boxes scrolls the containing page instead of trapping the event or changing selection; popup choices stay left-aligned and equal in width to the combo box.
- Keep explanations in question-mark tooltips. Visible helper copy should be short and only retained when it makes an otherwise hidden interaction discoverable.
- Keep the subtitle encoder selector in the compact output-settings row; expose automatic, CPU, NVIDIA, Intel, and AMD choices while explaining availability and fallback behavior through its question-mark tooltip.
- Keep the X downloader in a third compact tab. Use a visible URL label, adjacent paste/parse actions, an internally scrolling media list with explicit checkboxes, and one quality selector per media item.
- During X parsing and downloading, keep status text and progress visible in the shared header, disable conflicting primary actions, and retain a clear cancel route. Show network and validation failures as concise Chinese text with a recovery action implied by the affected control.
- Prefer direct MP4 qualities before HLS at equivalent resolution. Represent an X animated image as “动图（MP4）” and never imply that a GIF file will be produced.
- Keep contextual progress and the current tab's primary/cancel actions in the compact top header. Place the FFmpeg selector and engine path in a thin strip above each tab's first input section.
- Collapse the engine strip's error row when no error exists so the ready state, path, and selector remain vertically centered; allocate the second row only while an error message is visible.
- At the default 780×540 window size, all tabs should fit without a page-level vertical scrollbar; the X media list may scroll internally when a post contains several items. Page scrolling remains available at smaller user-resized dimensions.
- Do not use color alone for errors or progress; retain the accompanying status text.
- Keep image inspection in a fourth compact tab with only dimensions, shooting metadata, and geolocation. Use three read-only sections, no FFmpeg selector, conversion buttons, or unrelated metadata. Allow text selection and tooltips for long values; retain page scrolling at reduced window sizes. Show loading/errors explicitly and never substitute file timestamps or guessed coordinates for absent metadata.
- Put the manual address lookup/cancel action in the geolocation section header. Show city/district and nearby address as selectable read-only values. Require a clear confirmation before sharing GPS with Photon; no automatic geolocation requests on image load. Keep source attribution and nearby-match caveat visible, preserve full details in tooltips, and clear/cancel stale results when the image changes.
- Image metadata uses compact 20px read-only rows, 6px horizontal / 4px vertical section padding, and 4px section gaps so address rows fit at 780×540 without shrinking the 13px value text or the 26px action buttons.
- Keep visual GIF trimming inside the existing collapsed-by-default time-range expander, never a separate window. Expand into a 180px-high silent source-video preview, thumbnail range selection with two handles, an independent seek track, playback controls, and synchronized time fields. Allow vertical page scrolling when expanded; retain the compact no-scroll default while collapsed. Reserve busy-indicator space so pointer targets do not move during decoding. Pause hidden or disabled previews and retain the selected range. Preserve keyboard operation and explicit labels for the timeline and time inputs.
- Range handles are 8×18 DIP outward-facing tabs attached to 1.5 DIP boundary lines. Start protrudes left and end right; hit padding stays outside the selection. Do not render text or tooltips over handles/video. Image dragging only scrubs; only boundary handles change the range. Preserve the pointer grab offset, freeze the playhead during boundary editing, and snap at 8 DIP with a 12 DIP release threshold. Give snap feedback on the reference line without shifting layout. Support narrow ranges and handles at both video ends.
- Use four 28×26 DIP native-semantic transport buttons with 16 DIP code-native vector icons: play/pause, restart, repeat toggle, reset full range. Keep short function tooltips and accessible names on these icon buttons. The repeat toggle has a clear checked appearance; keyboard focus remains visible only during keyboard navigation. Timeline arrow-key steps, including Shift/Ctrl boundary editing, are 0.05 seconds.
- Scrubbing must continue to produce frames while the pointer moves, using a bounded latest-pending queue and immediate cache hits. On release request an exact frame without an extra debounce delay. Do not let late fast frames replace newer/cached/final frames, and prioritize interactive decoding over thumbnail loading. Display boundary-preview time separately from the fixed playhead reference.

## Avoid

- Dark mode as the default visual theme.
- Rounded cards, pill controls, heavy shadows, gradients, glass effects, or large colored CTAs.
- Changing the existing feature layout merely to imitate a reference screenshot.
