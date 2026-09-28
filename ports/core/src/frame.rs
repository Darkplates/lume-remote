use crate::wire::{Packet, Reader};
use anyhow::{Result, bail, ensure};
use image::{ImageFormat, ImageReader, RgbaImage};
use std::io::Cursor;

const MAX_PIXELS: u64 = 33_554_432;
pub fn dimensions(w: i32, h: i32) -> Result<(u32, u32)> {
    ensure!(
        w > 0 && h > 0 && w <= 16384 && h <= 16384 && w as u64 * h as u64 <= MAX_PIXELS,
        "Desktop dimensions exceed the bound"
    );
    Ok((w as u32, h as u32))
}
#[derive(Default)]
pub struct Decoder {
    image: Option<RgbaImage>,
    pub epoch: i32,
    pub sequence: i32,
}
impl Decoder {
    pub fn apply(&mut self, packet: &Packet, version: i32) -> Result<Option<RgbaImage>> {
        let mut r = Reader::new(&packet.0[1..]);
        let epoch = if version >= 4 { r.int()? } else { 1 };
        let seq = r.int()?;
        let (w, h) = dimensions(r.int()?, r.int()?)?;
        let count = r.int()?;
        ensure!(
            epoch > 0 && epoch >= self.epoch && seq > self.sequence && (1..=24).contains(&count),
            "Invalid frame sequence or region count"
        );
        let reset =
            self.epoch != epoch || self.image.as_ref().is_none_or(|i| i.dimensions() != (w, h));
        if reset {
            ensure!(count == 1, "First frame must be complete");
            self.image = None;
        }
        let mut patches = Vec::with_capacity(count as usize);
        let mut pixels = 0u64;
        let mut unsupported = false;
        for _ in 0..count {
            let x = r.int()?;
            let y = r.int()?;
            let (pw, ph) = dimensions(r.int()?, r.int()?)?;
            let codec = r.byte()?;
            let n = r.int()?;
            ensure!(
                x >= 0
                    && y >= 0
                    && x as u64 + pw as u64 <= w as u64
                    && y as u64 + ph as u64 <= h as u64,
                "Invalid frame region"
            );
            ensure!(
                !reset || (x == 0 && y == 0 && pw == w && ph == h),
                "Incomplete reset frame"
            );
            pixels += pw as u64 * ph as u64;
            ensure!(
                pixels <= w as u64 * h as u64,
                "Frame regions exceed the desktop"
            );
            ensure!(
                n >= 4 && n as u64 <= pw as u64 * ph as u64 * 4 + 65536,
                "Invalid image block size"
            );
            let bytes = r.take(n as usize)?;
            let format = match codec {
                0 => ImageFormat::Jpeg,
                3 => ImageFormat::Png,
                1 | 2 => {
                    unsupported = true;
                    continue;
                }
                _ => bail!("Unknown frame codec"),
            };
            let size = ImageReader::with_format(Cursor::new(bytes), format).into_dimensions()?;
            ensure!(size == (pw, ph), "Image header does not match the region");
            let patch = ImageReader::with_format(Cursor::new(bytes), format)
                .decode()?
                .into_rgba8();
            patches.push((x as u32, y as u32, patch));
        }
        r.end()?;
        self.epoch = epoch;
        self.sequence = seq;
        // Older Windows hosts may send one XPRESS/H.264 frame before JPEG quality
        // takes effect. Acknowledge it, but never display a partly decoded desktop.
        if unsupported {
            self.image = None;
            return Ok(None);
        }
        let canvas = self.image.get_or_insert_with(|| RgbaImage::new(w, h));
        for (x, y, patch) in patches {
            image::imageops::replace(canvas, &patch, x as i64, y as i64);
        }
        Ok(Some(canvas.clone()))
    }
}
pub fn encode(
    frame: &RgbaImage,
    sequence: i32,
    epoch: i32,
    version: i32,
    lossless: bool,
    jpeg: u8,
) -> Result<Packet> {
    dimensions(frame.width() as i32, frame.height() as i32)?;
    let mut encoded = Vec::new();
    if lossless {
        frame.write_to(&mut Cursor::new(&mut encoded), ImageFormat::Png)?;
    } else {
        let rgb = image::DynamicImage::ImageRgba8(frame.clone()).into_rgb8();
        image::codecs::jpeg::JpegEncoder::new_with_quality(&mut encoded, jpeg).encode(
            rgb.as_raw(),
            rgb.width(),
            rgb.height(),
            image::ExtendedColorType::Rgb8,
        )?;
    }
    let p = if version >= 4 {
        Packet::new(4).int(epoch)
    } else {
        Packet::new(4)
    };
    Ok(p.int(sequence)
        .int(frame.width() as i32)
        .int(frame.height() as i32)
        .int(1)
        .int(0)
        .int(0)
        .int(frame.width() as i32)
        .int(frame.height() as i32)
        .byte(if lossless { 3 } else { 0 })
        .int(encoded.len() as i32)
        .bytes(&encoded))
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn exact_portable_source() {
        let source = RgbaImage::from_fn(53, 29, |x, y| {
            image::Rgba([x as u8 * 3, y as u8 * 7, 40, 255])
        });
        let p = encode(&source, 1, 2, 4, true, 85).unwrap();
        let mut d = Decoder::default();
        assert_eq!(d.apply(&p, 4).unwrap().unwrap(), source);
        assert_eq!(d.epoch, 2);
        assert!(d.apply(&p, 4).is_err());
        let stale = encode(&source, 2, 1, 4, true, 85).unwrap();
        assert!(
            d.apply(&stale, 4).is_err(),
            "A newer sequence must not revive an old display"
        );
    }
    #[test]
    fn jpeg_geometry() {
        let source = RgbaImage::from_pixel(64, 48, image::Rgba([20, 120, 190, 255]));
        let p = encode(&source, 1, 1, 4, false, 90).unwrap();
        let out = Decoder::default().apply(&p, 4).unwrap().unwrap();
        assert_eq!(out.dimensions(), source.dimensions());
        for (a, b) in out.get_pixel(4, 4).0.iter().zip(source.get_pixel(4, 4).0) {
            assert!((*a as i16 - b as i16).abs() < 6);
        }
    }
    #[test]
    fn malformed_dimensions_and_payload() {
        assert!(dimensions(16384, 16384).is_err());
        assert!(dimensions(-1, 1).is_err());
        let p = Packet::new(4).int(1).int(1).int(64).int(64).int(25);
        assert!(Decoder::default().apply(&p, 4).is_err());
    }
}
