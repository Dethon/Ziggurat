//! The icons, drawn rather than shipped: a microphone for each tray state, and the executable's
//! own icon around the same microphone.
//!
//! Drawn so that one routine serves both ends. The tray asks for whatever size the shell uses at
//! the screen's scale — a 16x16 bitmap stretched to 24 is what made the old discs blurry — and
//! `build.rs` asks for every size an `.ico` carries and links them into the executable. The
//! build script compiles this file as a module of its own, which is why it reaches nothing else in
//! the crate and needs nothing beyond `std`.
//!
//! The pixels are the bytes of an icon resource, which `CreateIconFromResourceEx` reads directly
//! and an `.ico` entry stores verbatim, so what the tray shows and what Explorer shows cannot drift
//! apart.

/// What to draw.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Art {
    Idle,
    Recording,
    Transcribing,
    /// Struck through rather than only recoloured, so it differs from recording by shape as well:
    /// the two reds are not something everybody can tell apart.
    Failing,
    /// The executable's icon: the microphone, white, on a blue tile.
    App,
}

/// The sizes the executable's icon carries: the shell's small and large icon at every common
/// scale, and the larger views. Bigger ones are scaled down from these rather than carried.
pub const APP_SIZES: [u32; 9] = [16, 20, 24, 32, 40, 48, 64, 96, 128];

/// The resource id of the executable's icon group. Explorer shows the lowest-numbered group.
pub const APP_GROUP_ID: u16 = 1;

/// Straight-alpha RGBA, top row first.
pub type Pixel = [u8; 4];

/// Every sample is taken this many times along each axis, which is what anti-aliases the edges.
const SUPERSAMPLE: u32 = 4;

const GREY: [f32; 3] = [0x8C as f32, 0x8C as f32, 0x8C as f32];
const RED: [f32; 3] = [0xE5 as f32, 0x38 as f32, 0x3B as f32];
const AMBER: [f32; 3] = [0xF0 as f32, 0xA0 as f32, 0x30 as f32];
const WHITE: [f32; 3] = [255.0, 255.0, 255.0];
const TILE_TOP: [f32; 3] = [0x4C as f32, 0x6E as f32, 0xF5 as f32];
const TILE_BOTTOM: [f32; 3] = [0x34 as f32, 0x4B as f32, 0xC4 as f32];

/// The picture at `size` x `size` pixels.
pub fn pixels(art: Art, size: u32) -> Vec<Pixel> {
    let mut out = Vec::with_capacity((size * size) as usize);
    let samples = (SUPERSAMPLE * SUPERSAMPLE) as f32;
    for row in 0..size {
        for column in 0..size {
            // Premultiplied while accumulating, so an edge sample's colour is weighted by how much
            // of it is there; straightened again once the pixel is done.
            let mut sum = [0.0f32; 4];
            for sy in 0..SUPERSAMPLE {
                for sx in 0..SUPERSAMPLE {
                    let x = column as f32 + (sx as f32 + 0.5) / SUPERSAMPLE as f32;
                    let y = row as f32 + (sy as f32 + 0.5) / SUPERSAMPLE as f32;
                    if let Some(rgb) = sample(art, size as f32, x, y) {
                        sum[0] += rgb[0];
                        sum[1] += rgb[1];
                        sum[2] += rgb[2];
                        sum[3] += 1.0;
                    }
                }
            }
            out.push(if sum[3] == 0.0 {
                [0, 0, 0, 0]
            } else {
                [
                    (sum[0] / sum[3]).round() as u8,
                    (sum[1] / sum[3]).round() as u8,
                    (sum[2] / sum[3]).round() as u8,
                    (sum[3] / samples * 255.0).round() as u8,
                ]
            });
        }
    }
    out
}

/// The colour at one point, in pixels from the top-left corner of a `size`-pixel square, or
/// nothing if the point is transparent.
fn sample(art: Art, size: f32, x: f32, y: f32) -> Option<[f32; 3]> {
    match art {
        Art::Idle => microphone(size, x, y).then_some(GREY),
        Art::Recording => microphone(size, x, y).then_some(RED),
        Art::Transcribing => microphone(size, x, y).then_some(AMBER),
        Art::Failing => {
            let (from, to) = ((0.14 * size, 0.10 * size), (0.86 * size, 0.90 * size));
            let stroke = (0.055 * size).max(0.8);
            if capsule(x, y, from, to, stroke) {
                Some(RED)
            } else {
                // A gap either side of the stroke, so the slash reads as crossing the microphone
                // instead of merging into it at 16 pixels.
                (microphone(size, x, y)
                    && !capsule(x, y, from, to, stroke + (0.065 * size).max(1.0)))
                .then_some(RED)
            }
        }
        Art::App => {
            if !rounded_square(size, x, y, 0.03 * size, 0.22 * size) {
                return None;
            }
            // The microphone sits in a smaller square of the same parity, so its centre line
            // stays on the same pixel boundary as the tile's.
            let inner = (0.72 * size / 2.0).round() * 2.0 - size % 2.0;
            let offset = (size - inner) / 2.0;
            if microphone(inner, x - offset, y - offset) {
                return Some(WHITE);
            }
            let t = (y / size).clamp(0.0, 1.0);
            Some(std::array::from_fn(|i| {
                TILE_TOP[i] + (TILE_BOTTOM[i] - TILE_TOP[i]) * t
            }))
        }
    }
}

/// A studio microphone in a `size`-pixel square: the capsule, the cradle under it, the stem and
/// the foot.
///
/// Measured in whole pixels rather than in fractions of the square, because at 16 pixels a
/// capsule whose edges fall mid-pixel smears into the cradle beside it. The capsule is an even
/// number of pixels wide on an even square, so both its edges land on a pixel boundary, and the
/// gap around it never shrinks below one whole pixel.
fn microphone(size: f32, x: f32, y: f32) -> bool {
    let centre = size / 2.0;
    let stroke = (0.09 * size).max(1.5) / 2.0;
    let head = (0.16 * size - 0.3).round().max(2.0);
    let gap = (0.06 * size).round().max(1.0);
    let top = (0.08 * size).round();
    let pivot = (0.43 * size).round();
    let cradle = head + gap + stroke;
    let arms = pivot - (0.07 * size).round().max(1.0);
    let foot = size - (0.08 * size).round() - stroke;
    let half_foot = (0.17 * size).round();

    capsule(x, y, (centre, top + head), (centre, pivot), head)
        || (y >= pivot && (distance((x, y), (centre, pivot)) - cradle).abs() <= stroke)
        || capsule(
            x,
            y,
            (centre - cradle, arms),
            (centre - cradle, pivot),
            stroke,
        )
        || capsule(
            x,
            y,
            (centre + cradle, arms),
            (centre + cradle, pivot),
            stroke,
        )
        || capsule(x, y, (centre, pivot + cradle), (centre, foot), stroke)
        || capsule(
            x,
            y,
            (centre - half_foot, foot),
            (centre + half_foot, foot),
            stroke,
        )
}

/// Within `radius` of the segment from `a` to `b`.
fn capsule(x: f32, y: f32, a: (f32, f32), b: (f32, f32), radius: f32) -> bool {
    let (dx, dy) = (b.0 - a.0, b.1 - a.1);
    let length = dx * dx + dy * dy;
    let t = if length == 0.0 {
        0.0
    } else {
        (((x - a.0) * dx + (y - a.1) * dy) / length).clamp(0.0, 1.0)
    };
    distance((x, y), (a.0 + t * dx, a.1 + t * dy)) <= radius
}

fn rounded_square(size: f32, x: f32, y: f32, inset: f32, corner: f32) -> bool {
    let (low, high) = (inset + corner, size - inset - corner);
    let nearest = (x.clamp(low, high), y.clamp(low, high));
    distance((x, y), nearest) <= corner
}

fn distance(a: (f32, f32), b: (f32, f32)) -> f32 {
    ((a.0 - b.0).powi(2) + (a.1 - b.1).powi(2)).sqrt()
}

const BITMAPINFOHEADER_BYTES: usize = 40;

/// One icon image as Windows stores it: a `BITMAPINFOHEADER` declaring twice the height, the
/// colour rows bottom-up as straight-alpha BGRA, then the 1-bit AND mask, bottom-up too and padded
/// to whole 32-bit words per row. A set mask bit means transparent, which only something that
/// ignores the alpha channel ever reads.
pub fn icon_image(art: Art, size: u32) -> Vec<u8> {
    let picture = pixels(art, size);
    let side = size as usize;
    let mask_row = side.div_ceil(32) * 4;
    let mut out = Vec::with_capacity(BITMAPINFOHEADER_BYTES + side * side * 4 + mask_row * side);

    out.extend_from_slice(&(BITMAPINFOHEADER_BYTES as u32).to_le_bytes());
    out.extend_from_slice(&(size as i32).to_le_bytes());
    out.extend_from_slice(&(2 * size as i32).to_le_bytes());
    out.extend_from_slice(&1u16.to_le_bytes()); // planes
    out.extend_from_slice(&32u16.to_le_bytes()); // bits per pixel
    out.extend_from_slice(&[0; 24]); // uncompressed; size, resolution and palette all unset

    for row in (0..side).rev() {
        for &[r, g, b, a] in &picture[row * side..(row + 1) * side] {
            out.extend_from_slice(&[b, g, r, a]);
        }
    }
    for row in (0..side).rev() {
        let mut bits = vec![0u8; mask_row];
        for column in 0..side {
            if picture[row * side + column][3] == 0 {
                bits[column / 8] |= 0x80 >> (column % 8);
            }
        }
        out.extend_from_slice(&bits);
    }
    out
}

const RT_ICON: u16 = 3;
const RT_GROUP_ICON: u16 = 14;
/// `MOVEABLE | PURE | DISCARDABLE`, what `rc.exe` writes for icons and their group.
const ICON_FLAGS: u16 = 0x1030;
const RESOURCE_HEADER_BYTES: u32 = 32;

/// A compiled resource file (`.res`) holding the executable's icon at every size in `sizes`.
///
/// Written by hand because the linker takes a `.res` directly, so no resource compiler has to
/// exist in the cross-compiling toolchain. The format is a run of entries, each a fixed header
/// naming its type and id by number followed by its data padded to a 32-bit boundary, behind one
/// empty entry that marks the file as 32-bit.
pub fn resource_file(art: Art, sizes: &[u32]) -> Vec<u8> {
    let mut out = Vec::new();
    write_resource(&mut out, 0, 0, 0, &[]);

    let mut group = Vec::new();
    group.extend_from_slice(&0u16.to_le_bytes()); // reserved
    group.extend_from_slice(&1u16.to_le_bytes()); // icons, not cursors
    group.extend_from_slice(&(sizes.len() as u16).to_le_bytes());
    for (index, &size) in sizes.iter().enumerate() {
        let id = index as u16 + 1;
        let image = icon_image(art, size);
        write_resource(&mut out, RT_ICON, id, ICON_FLAGS, &image);

        // A width of 256 is written as 0; nothing this draws is that large, but the byte cannot
        // hold it either way.
        let side = if size >= 256 { 0 } else { size as u8 };
        group.extend_from_slice(&[side, side, 0, 0]); // width, height, palette, reserved
        group.extend_from_slice(&1u16.to_le_bytes()); // planes
        group.extend_from_slice(&32u16.to_le_bytes()); // bits per pixel
        group.extend_from_slice(&(image.len() as u32).to_le_bytes());
        group.extend_from_slice(&id.to_le_bytes());
    }
    write_resource(&mut out, RT_GROUP_ICON, APP_GROUP_ID, ICON_FLAGS, &group);
    out
}

fn write_resource(out: &mut Vec<u8>, kind: u16, id: u16, flags: u16, data: &[u8]) {
    out.extend_from_slice(&(data.len() as u32).to_le_bytes());
    out.extend_from_slice(&RESOURCE_HEADER_BYTES.to_le_bytes());
    out.extend_from_slice(&[0xFF, 0xFF]);
    out.extend_from_slice(&kind.to_le_bytes());
    out.extend_from_slice(&[0xFF, 0xFF]);
    out.extend_from_slice(&id.to_le_bytes());
    out.extend_from_slice(&0u32.to_le_bytes()); // data version
    out.extend_from_slice(&flags.to_le_bytes());
    out.extend_from_slice(&0u16.to_le_bytes()); // language neutral
    out.extend_from_slice(&0u32.to_le_bytes()); // version
    out.extend_from_slice(&0u32.to_le_bytes()); // characteristics
    out.extend_from_slice(data);
    out.resize(out.len().next_multiple_of(4), 0);
}

#[cfg(test)]
mod tests {
    use super::*;

    const TRAY: [Art; 4] = [Art::Idle, Art::Recording, Art::Transcribing, Art::Failing];

    fn at(picture: &[Pixel], size: u32, x: f32, y: f32) -> Pixel {
        let column = ((x * size as f32) as u32).min(size - 1);
        let row = ((y * size as f32) as u32).min(size - 1);
        picture[(row * size + column) as usize]
    }

    fn opacity(picture: &[Pixel]) -> Vec<u8> {
        picture.iter().map(|pixel| pixel[3]).collect()
    }

    #[test]
    fn a_picture_has_one_pixel_per_point_at_the_size_asked_for() {
        for size in [16, 20, 24, 32] {
            assert_eq!(pixels(Art::Idle, size).len(), (size * size) as usize);
        }
    }

    #[test]
    fn the_tray_microphone_is_solid_in_its_head_and_clear_in_its_corners() {
        for art in TRAY {
            let picture = pixels(art, 32);
            assert_eq!(at(&picture, 32, 0.02, 0.02)[3], 0, "{art:?}");
            assert_eq!(at(&picture, 32, 0.98, 0.98)[3], 0, "{art:?}");
        }
        // The failing icon's slash runs through the centre, so its head is checked off it.
        assert_eq!(
            at(&pixels(Art::Recording, 32), 32, 0.5, 0.3),
            [0xE5, 0x38, 0x3B, 255]
        );
        assert_eq!(
            at(&pixels(Art::Transcribing, 32), 32, 0.5, 0.3),
            [0xF0, 0xA0, 0x30, 255]
        );
    }

    #[test]
    fn its_edges_are_smoothed_rather_than_stepped() {
        let partial = pixels(Art::Idle, 16)
            .iter()
            .filter(|p| p[3] > 0 && p[3] < 255)
            .count();
        assert!(partial > 10, "only {partial} partly covered pixels");
    }

    #[test]
    fn recording_and_transcribing_differ_by_colour_and_failing_by_shape_too() {
        let recording = pixels(Art::Recording, 16);
        let transcribing = pixels(Art::Transcribing, 16);
        let failing = pixels(Art::Failing, 16);
        assert_eq!(opacity(&recording), opacity(&transcribing));
        assert_ne!(recording, transcribing);
        assert_ne!(opacity(&recording), opacity(&failing));
    }

    #[test]
    fn the_app_icon_is_a_filled_tile_with_the_microphone_on_it() {
        let picture = pixels(Art::App, 48);
        assert_eq!(
            at(&picture, 48, 0.5, 0.5 - 0.72 * 0.2)[..],
            [255, 255, 255, 255]
        );
        let tile = at(&picture, 48, 0.12, 0.5);
        assert_eq!(tile[3], 255);
        assert!(tile[2] > tile[0], "the tile is blue, not {tile:?}");
        assert_eq!(
            at(&picture, 48, 0.0, 0.0)[3],
            0,
            "the tile's corners are rounded off"
        );
    }

    #[test]
    fn an_icon_image_is_a_double_height_bitmap_then_its_mask() {
        let image = icon_image(Art::Recording, 20);
        let mask_row = 4; // 20 bits round up to one 32-bit word
        assert_eq!(image.len(), 40 + 20 * 20 * 4 + mask_row * 20);
        assert_eq!(u32::from_le_bytes(image[0..4].try_into().unwrap()), 40);
        assert_eq!(i32::from_le_bytes(image[4..8].try_into().unwrap()), 20);
        assert_eq!(i32::from_le_bytes(image[8..12].try_into().unwrap()), 40);
        assert_eq!(u16::from_le_bytes(image[14..16].try_into().unwrap()), 32);
    }

    #[test]
    fn an_icon_image_stores_the_bottom_row_first_in_blue_green_red_order() {
        let size = 24;
        let picture = pixels(Art::Transcribing, size);
        let image = icon_image(Art::Transcribing, size);
        let side = size as usize;
        let first_stored = &image[40..40 + side * 4];
        let bottom: Vec<u8> = picture[(side - 1) * side..]
            .iter()
            .flat_map(|&[r, g, b, a]| [b, g, r, a])
            .collect();
        assert_eq!(first_stored, &bottom[..]);
    }

    #[test]
    fn the_mask_marks_exactly_the_transparent_pixels() {
        let size = 16usize;
        let picture = pixels(Art::Idle, size as u32);
        let image = icon_image(Art::Idle, size as u32);
        let mask = &image[40 + size * size * 4..];
        for row in 0..size {
            let stored = &mask[(size - 1 - row) * 4..];
            for column in 0..size {
                let transparent = stored[column / 8] & (0x80 >> (column % 8)) != 0;
                assert_eq!(
                    transparent,
                    picture[row * size + column][3] == 0,
                    "{row},{column}"
                );
            }
        }
    }

    /// The entries a `.res` holds, as (type, id, data), read the way the linker reads them.
    fn entries(file: &[u8]) -> Vec<(u16, u16, Vec<u8>)> {
        let mut found = Vec::new();
        let mut at = 0;
        while at < file.len() {
            assert_eq!(at % 4, 0, "entries start on a 32-bit boundary");
            let word = |offset: usize| {
                u32::from_le_bytes(file[at + offset..at + offset + 4].try_into().unwrap())
            };
            let half = |offset: usize| {
                u16::from_le_bytes(file[at + offset..at + offset + 2].try_into().unwrap())
            };
            let (size, header) = (word(0) as usize, word(4) as usize);
            assert_eq!(header, 32);
            assert_eq!(
                (half(8), half(12)),
                (0xFFFF, 0xFFFF),
                "type and id are numbers"
            );
            found.push((
                half(10),
                half(14),
                file[at + header..at + header + size].to_vec(),
            ));
            at = (at + header + size).next_multiple_of(4);
        }
        found
    }

    #[test]
    fn the_resource_file_opens_with_the_empty_entry_that_marks_it_32_bit() {
        let file = resource_file(Art::App, &[16]);
        assert_eq!(entries(&file)[0], (0, 0, vec![]));
    }

    #[test]
    fn the_resource_file_holds_one_icon_per_size_and_a_group_naming_them_all() {
        let sizes = [16, 24, 32];
        let found = entries(&resource_file(Art::App, &sizes));
        assert_eq!(found.len(), 1 + sizes.len() + 1);

        let (kind, id, group) = found.last().unwrap();
        assert_eq!((*kind, *id), (RT_GROUP_ICON, APP_GROUP_ID));
        assert_eq!(&group[0..6], &[0, 0, 1, 0, 3, 0]);

        for (index, &size) in sizes.iter().enumerate() {
            let (kind, id, data) = &found[1 + index];
            assert_eq!(*kind, RT_ICON);
            assert_eq!(data, &icon_image(Art::App, size));

            let entry = &group[6 + index * 14..6 + (index + 1) * 14];
            assert_eq!((entry[0], entry[1]), (size as u8, size as u8));
            assert_eq!(u16::from_le_bytes([entry[6], entry[7]]), 32);
            assert_eq!(
                u32::from_le_bytes(entry[8..12].try_into().unwrap()) as usize,
                data.len()
            );
            assert_eq!(u16::from_le_bytes([entry[12], entry[13]]), *id);
        }
    }
}
