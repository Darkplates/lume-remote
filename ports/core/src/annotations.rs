//! The existing Windows annotation wire format, independent of UI coordinates.
use crate::wire::{Packet, Reader};
use anyhow::{Result, ensure};
pub const CAPABILITY: u64 = 64;
pub fn validate(epoch: i32, points: &[[i32; 2]]) -> Result<()> {
    ensure!(
        epoch > 0 && (points.is_empty() || (2..=128).contains(&points.len())),
        "Draw 2–128 points on the current display"
    );
    ensure!(
        points.iter().flatten().all(|v| (0..=65535).contains(v)),
        "Annotation coordinates are outside the display"
    );
    Ok(())
}
pub fn packet(id: i64, epoch: i32, points: &[[i32; 2]]) -> Result<Packet> {
    validate(epoch, points)?;
    ensure!(id > 0, "Invalid annotation request");
    let mut p = Packet::new(18)
        .long(id)
        .byte(7)
        .int(epoch)
        .int(points.len() as i32);
    for point in points {
        p = p.int(point[0]).int(point[1]);
    }
    Ok(p)
}
pub fn read(r: &mut Reader<'_>) -> Result<(i32, Vec<[i32; 2]>)> {
    let epoch = r.int()?;
    let count = r.int()?;
    ensure!(
        count == 0 || (2..=128).contains(&count),
        "Invalid annotation size"
    );
    let mut points = Vec::with_capacity(count as usize);
    for _ in 0..count {
        points.push([r.int()?, r.int()?]);
    }
    r.end()?;
    validate(epoch, &points)?;
    Ok((epoch, points))
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn wire_roundtrip_and_bounds() {
        for points in [vec![], vec![[0, 65535], [65535, 0]], vec![[14, 17]; 128]] {
            let p = packet(71, 4, &points).unwrap();
            let mut r = Reader::new(&p.0[10..]);
            assert_eq!(read(&mut r).unwrap(), (4, points));
        }
        for points in [
            vec![[0, 0]],
            vec![[0, 0]; 129],
            vec![[-1, 0]; 2],
            vec![[65536, 0]; 2],
        ] {
            assert!(packet(1, 1, &points).is_err());
        }
        assert!(packet(1, 0, &[]).is_err());
        let p = Packet::new(0).int(1).int(i32::MAX);
        assert!(read(&mut Reader::new(&p.0[1..])).is_err());
    }
}
