//! Authenticated permanent host. Native capture starts only for an authorized controller.
use crate::{
    host_store::{HostConfig, HostStore},
    paired::{self, Body, Broker, Envelope, SavedComputer},
    session::{Desktop, Host},
};
use anyhow::{Result, ensure};
use std::{
    collections::HashMap,
    path::PathBuf,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
        mpsc::{self, SyncSender},
    },
    thread,
    time::{Duration, Instant},
};

pub type Factory = Arc<dyn Fn(&HostConfig) -> Result<Box<dyn Desktop>> + Send + Sync>;
pub struct PermanentHost {
    pub status: Arc<Mutex<String>>,
    stop: Arc<AtomicBool>,
    worker: Option<thread::JoinHandle<()>>,
}
impl PermanentHost {
    pub fn start(store: Arc<HostStore>, library: PathBuf, factory: Factory) -> Result<Self> {
        ensure!(store.load()?.enabled, "Permanent access is disabled");
        let status = Arc::new(Mutex::new("Connecting host…".into()));
        let stop = Arc::new(AtomicBool::new(false));
        let state = status.clone();
        let cancel = stop.clone();
        let worker = thread::spawn(move || {
            if run(store, library, factory, &cancel, &state).is_err() {
                *state.lock().unwrap() =
                    "Host stopped. Check protected settings and restart access.".into();
            } else {
                *state.lock().unwrap() = "Permanent access stopped".into();
            }
        });
        Ok(Self {
            status,
            stop,
            worker: Some(worker),
        })
    }
    pub fn stop(&self) {
        self.stop.store(true, Ordering::Release);
    }
    pub fn is_finished(&self) -> bool {
        self.worker.as_ref().is_none_or(|w| w.is_finished())
    }
    pub fn close_and_wait(&mut self) {
        self.stop();
        if let Some(w) = self.worker.take() {
            let _ = w.join();
        }
    }
}
impl Drop for PermanentHost {
    fn drop(&mut self) {
        self.close_and_wait();
    }
}
struct Active {
    controller: SavedComputer,
    source: String,
    request: String,
    replies: SyncSender<String>,
    stop: Arc<AtomicBool>,
    worker: Option<thread::JoinHandle<()>>,
}
impl Drop for Active {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Release);
        if let Some(w) = self.worker.take() {
            let _ = w.join();
        }
    }
}
struct Outgoing {
    destination: String,
    envelope: Envelope,
}
fn response(
    config: &HostConfig,
    source: &str,
    env: &Envelope,
    key: &str,
    stage: &str,
    body: Body,
) -> Result<Outgoing> {
    Ok(Outgoing {
        destination: source.into(),
        envelope: paired::seal(
            key,
            &config.host,
            source,
            &env.route,
            &env.request,
            stage,
            body,
        )?,
    })
}
/// Signaling messages accepted from one broker source per second.
const SOURCE_BUDGET: u32 = 30;
/// Signaling messages read per 20 ms host tick.
const MESSAGES_PER_TICK: usize = 64;
/// Sources tracked at once. The oldest window is evicted, which can only reset
/// a sender's own budget; it never consumes another source's budget.
const MAX_SOURCES: usize = 256;
/// Per-source signaling budget, counted before authentication so one noisy
/// broker identity cannot starve connection requests from the others.
#[derive(Default)]
struct SourceBudget {
    windows: HashMap<String, (Instant, u32)>,
}
impl SourceBudget {
    fn allow(&mut self, source: &str, now: Instant) -> bool {
        if source.is_empty() || source.len() > 256 {
            return false;
        }
        let window = Duration::from_secs(1);
        if !self.windows.contains_key(source) && self.windows.len() >= MAX_SOURCES {
            self.windows
                .retain(|_, (started, _)| now.saturating_duration_since(*started) < window);
            if self.windows.len() >= MAX_SOURCES {
                if let Some(oldest) = self
                    .windows
                    .iter()
                    .min_by_key(|(_, (started, _))| *started)
                    .map(|(key, _)| key.clone())
                {
                    self.windows.remove(&oldest);
                }
            }
        }
        let (started, count) = self.windows.entry(source.to_owned()).or_insert((now, 0));
        if now.saturating_duration_since(*started) >= window {
            *started = now;
            *count = 0;
        }
        *count = count.saturating_add(1);
        *count <= SOURCE_BUDGET
    }
}
fn fresh(recent: &mut HashMap<String, i64>, source: &str, request: &str) -> bool {
    let now = paired::now();
    recent.retain(|_, until| *until > now);
    let key = format!("{source}:{request}");
    if recent.len() >= 256 || recent.contains_key(&key) {
        return false;
    }
    recent.insert(key, now + 3000000000);
    true
}
fn run(
    store: Arc<HostStore>,
    library: PathBuf,
    factory: Factory,
    stop: &AtomicBool,
    status: &Mutex<String>,
) -> Result<()> {
    let mut broker = None;
    let mut retry = Instant::now();
    let mut active: Option<Active> = None;
    let (out_tx, out_rx) = mpsc::sync_channel::<Outgoing>(8);
    let mut recent = HashMap::new();
    let mut budget = SourceBudget::default();
    let mut config = store.load()?;
    let mut checked = Instant::now();
    while !stop.load(Ordering::Acquire) {
        if checked.elapsed() >= Duration::from_millis(250) {
            config = store.load()?;
            checked = Instant::now();
        }
        if !config.enabled {
            break;
        }
        if active.as_ref().is_some_and(|a| {
            !config.authorized(&a.controller) || a.worker.as_ref().is_none_or(|w| w.is_finished())
        }) {
            active = None;
        }
        if broker.is_none() && Instant::now() >= retry {
            match Broker::connect_as(config.host.clone(), &config.broker_token, stop) {
                Ok(value) => {
                    broker = Some(value);
                    *status.lock().unwrap() = "Permanent access ready".into();
                }
                Err(_) => {
                    retry = Instant::now() + Duration::from_secs(10);
                    *status.lock().unwrap() = "Host waiting for network; retrying…".into();
                }
            }
        }
        if let Some(channel) = &mut broker {
            let result = (|| -> Result<()> {
                for _ in 0..8 {
                    if let Ok(out) = out_rx.try_recv() {
                        channel.send(&out.destination, out.envelope)?;
                    } else {
                        break;
                    }
                }
                // Drain a bounded batch so over-budget messages from one source
                // do not delay other sources' messages behind them.
                let mut reloaded = false;
                for _ in 0..MESSAGES_PER_TICK {
                    let Some(packet) = channel.read()? else {
                        break;
                    };
                    let Some(source) = packet["src"].as_str() else {
                        continue;
                    };
                    if !budget.allow(source, Instant::now()) {
                        continue;
                    }
                    if let Ok(env) =
                        serde_json::from_value::<Envelope>(packet["payload"]["metadata"].clone())
                    {
                        // Invalid authentication never tears down another person's host/session.
                        // Settings are re-read once per tick; store changes re-check atomically.
                        if !reloaded {
                            config = store.load()?;
                            reloaded = true;
                        }
                        let _ = receive(
                            &store,
                            &config,
                            &library,
                            &factory,
                            source,
                            env,
                            &mut recent,
                            &mut active,
                            &out_tx,
                        );
                    }
                }
                Ok(())
            })();
            if result.is_err() {
                broker = None;
                retry = Instant::now() + Duration::from_secs(2);
            }
        }
        if let Some(a) = &active {
            if !a.worker.as_ref().is_none_or(|w| w.is_finished()) {
                *status.lock().unwrap() = "Paired session active".into();
            }
        } else if broker.is_some() {
            *status.lock().unwrap() = "Host ready for paired computers".into();
        }
        thread::sleep(Duration::from_millis(20));
    }
    drop(active);
    Ok(())
}
#[allow(clippy::too_many_arguments)]
fn receive(
    store: &Arc<HostStore>,
    config: &HostConfig,
    library: &PathBuf,
    factory: &Factory,
    source: &str,
    env: Envelope,
    recent: &mut HashMap<String, i64>,
    active: &mut Option<Active>,
    out: &SyncSender<Outgoing>,
) -> Result<()> {
    if env.stage == "pair" {
        let pending = config
            .pending
            .as_ref()
            .ok_or_else(|| anyhow::anyhow!("No pending pairing"))?;
        ensure!(
            pending.id == env.route && pending.expires >= paired::now(),
            "Pairing expired"
        );
        let body = paired::open(&pending.key, source, &config.host, &env)?;
        let controller = SavedComputer {
            host: config.host.clone(),
            id: env.route.clone(),
            name: body.name.clone().unwrap_or_default(),
            key: body.key.clone().unwrap_or_default(),
        };
        controller.validate()?;
        ensure!(
            fresh(recent, source, &env.request),
            "Repeated pairing request"
        );
        store.change(|current| {
            ensure!(
                current.enabled
                    && current.pending.as_ref().is_some_and(|p| p.id == pending.id
                        && p.expires >= paired::now()
                        && crate::wire::equal(p.key.as_bytes(), pending.key.as_bytes())),
                "Pairing expired or already used"
            );
            ensure!(current.controllers.len() < 32, "Too many paired computers");
            current.controllers.push(controller.clone());
            current.pending = None;
            Ok(())
        })?;
        out.try_send(response(
            config,
            source,
            &env,
            &controller.key,
            "paired",
            Body {
                name: Some(config.name.clone()),
                code: None,
                key: None,
                message: None,
                mac: None,
                created: 0,
            },
        )?)
        .map_err(|_| anyhow::anyhow!("Host response queue is busy"))?;
        return Ok(());
    }
    let controller = config
        .controllers
        .iter()
        .find(|c| c.id == env.route)
        .ok_or_else(|| anyhow::anyhow!("Unknown controller"))?;
    let body = paired::open(&controller.key, source, &config.host, &env)?;
    if env.stage == "answer" {
        let a = active
            .as_ref()
            .ok_or_else(|| anyhow::anyhow!("No pending connection"))?;
        ensure!(
            a.controller.id == controller.id && a.source == source && a.request == env.request,
            "Answer belongs to another connection"
        );
        let code = body.code.as_deref().unwrap_or_default();
        ensure!(code.len() <= 65536, "Reply exceeds its bound");
        a.replies
            .try_send(code.into())
            .map_err(|_| anyhow::anyhow!("Answer queue is full"))?;
        return Ok(());
    }
    ensure!(
        env.stage == "connect" && fresh(recent, source, &env.request),
        "Invalid or repeated connection request"
    );
    if active.is_some() {
        out.try_send(response(
            config,
            source,
            &env,
            &controller.key,
            "error",
            Body {
                message: Some("busy".into()),
                name: None,
                code: None,
                key: None,
                mac: None,
                created: 0,
            },
        )?)
        .map_err(|_| anyhow::anyhow!("Host response queue is busy"))?;
        return Ok(());
    }
    let (replies, receiver) = mpsc::sync_channel(1);
    let stop = Arc::new(AtomicBool::new(false));
    let (store, library, factory, config, controller, source, out) = (
        store.clone(),
        library.clone(),
        factory.clone(),
        config.clone(),
        controller.clone(),
        source.to_owned(),
        out.clone(),
    );
    let mut a = Active {
        controller: controller.clone(),
        source: source.clone(),
        request: env.request.clone(),
        replies,
        stop: stop.clone(),
        worker: None,
    };
    a.worker = Some(thread::spawn(move || {
        let result = (|| -> Result<()> {
            let checked_store = store.clone();
            let checked_controller = controller.clone();
            let initial = config.clone();
            let mut host = Host::peer_resuming(
                &library,
                config.control,
                Arc::new(move || {
                    let current = checked_store.load()?;
                    ensure!(
                        current.authorized(&checked_controller)
                            && current.control == initial.control
                            && current.folder == initial.folder,
                        "Access was revoked or changed"
                    );
                    factory(&current)
                }),
                true,
                stop.clone(),
                Some(zeroize::Zeroizing::new(controller.key.clone())),
            )?;
            out.try_send(response(
                &config,
                &source,
                &env,
                &controller.key,
                "offer",
                Body {
                    code: Some(host.code.clone()),
                    name: None,
                    key: None,
                    message: None,
                    mac: None,
                    created: 0,
                },
            )?)
            .map_err(|_| anyhow::anyhow!("Host response queue is busy"))?;
            let started = Instant::now();
            let mut approved = false;
            while !stop.load(Ordering::Acquire) && !host.is_finished() {
                // Revocation stays independent of the rendezvous connection and the UI.
                let current = store.load()?;
                ensure!(
                    current.authorized(&controller)
                        && current.control == config.control
                        && current.folder == config.folder,
                    "Access was revoked or changed"
                );
                if let Ok(reply) = receiver.try_recv() {
                    host.accept_reply(reply)?;
                }
                if let Ok(request) = host.requests.try_recv() {
                    request.answer.try_send(true)?;
                    approved = true;
                }
                ensure!(
                    approved || started.elapsed() < Duration::from_secs(100),
                    "Paired connection timed out"
                );
                thread::sleep(Duration::from_millis(50));
            }
            host.close_and_wait();
            Ok(())
        })();
        let _ = result;
        stop.store(true, Ordering::Release);
    }));
    *active = Some(a);
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn signaling_budget_is_per_source_and_bounded() {
        let mut budget = SourceBudget::default();
        let now = Instant::now();
        for _ in 0..SOURCE_BUDGET {
            assert!(budget.allow("noisy", now));
        }
        for _ in 0..1000 {
            assert!(!budget.allow("noisy", now));
        }
        // One flooding source never consumes another source's budget.
        for _ in 0..SOURCE_BUDGET {
            assert!(budget.allow("owner", now));
        }
        assert!(!budget.allow("owner", now));
        // Many sources stay bounded; eviction only resets a sender's own window.
        for n in 0..MAX_SOURCES * 4 {
            assert!(budget.allow(&format!("source-{n}"), now));
            assert!(budget.windows.len() <= MAX_SOURCES);
        }
        assert!(budget.allow("late", now));
        assert!(!budget.allow("", now));
        assert!(!budget.allow(&"x".repeat(257), now));
        let later = now + Duration::from_secs(1);
        for _ in 0..SOURCE_BUDGET {
            assert!(budget.allow("noisy", later));
        }
        assert!(!budget.allow("noisy", later));
    }
    #[test]
    fn replay_window_is_bounded_and_identity_scoped() {
        let mut recent = HashMap::new();
        assert!(fresh(&mut recent, "a", "one"));
        assert!(!fresh(&mut recent, "a", "one"));
        assert!(fresh(&mut recent, "b", "one"));
        for n in 0..254 {
            assert!(fresh(&mut recent, "a", &n.to_string()));
        }
        assert!(!fresh(&mut recent, "c", "extra"));
        recent.values_mut().for_each(|v| *v = 0);
        assert!(fresh(&mut recent, "c", "extra"));
    }
}
