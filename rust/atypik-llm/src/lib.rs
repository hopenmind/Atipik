use std::{
    ffi::{CStr, CString},
    num::NonZeroU32,
    os::raw::{c_char, c_int},
    sync::Mutex,
};

use anyhow::Result;
use llama_cpp_2::{
    context::params::LlamaContextParams,
    context::LlamaContext,
    llama_backend::LlamaBackend,
    llama_batch::LlamaBatch,
    model::{params::LlamaModelParams, AddBos, LlamaChatMessage, LlamaModel, Special},
    sampling::LlamaSampler,
    token::LlamaToken,
};
use once_cell::sync::OnceCell;

// -- Engine ------------------------------------------------------------------

/// One mode = one persistent context whose system prompt is evaluated ONCE and
/// reused across calls. Per message we only rewind the user/generation tokens
/// (everything after the system prompt) and re-run, so the expensive system KV
/// is never recomputed. The model is a stateless transform, not a chatbot.
struct Mode {
    ctx: LlamaContext<'static>,
    system: String,
    sys_tokens: Vec<LlamaToken>,
    sys_len: i32,
}

struct Engine {
    backend: LlamaBackend,
    model: &'static LlamaModel,
    n_threads: i32,
    correct: Mode,
    rewrite: Option<Mode>,
    rewrite_prompt: String,
}

static ENGINE: OnceCell<Mutex<Engine>> = OnceCell::new();

// LlamaContext holds a raw FFI pointer (not Send/Sync by default), but every
// access is serialized through the Mutex, so it is sound to move/share the
// engine across threads.
unsafe impl Send for Engine {}
unsafe impl Sync for Engine {}

const REWRITE_SYSTEM_PROMPT: &str = "\
You are a tone filter inside an accessibility device for atypical typists. \
Keep the user's meaning and intent; remove only the aggression and profanity; \
return a calm, neutral version they can actually send. Work in the input's \
language. Never refuse, never add commentary or advice. If the input is pure \
hostility with nothing to deliver, output exactly: ***";

// -- Exported C API ----------------------------------------------------------

#[no_mangle]
pub extern "C" fn atypik_init(
    model_path: *const c_char,
    system_prompt: *const c_char,
    _keep_context: c_int,
) -> c_int {
    let Ok(path) = (unsafe { CStr::from_ptr(model_path) }).to_str() else { return -1 };
    let Ok(prompt) = (unsafe { CStr::from_ptr(system_prompt) }).to_str() else { return -1 };

    match load_engine(path, prompt) {
        Ok(engine) => {
            let _ = ENGINE.set(Mutex::new(engine));
            0
        }
        Err(_) => -1,
    }
}

#[no_mangle]
pub extern "C" fn atypik_set_rewrite_prompt(prompt: *const c_char) -> c_int {
    let Ok(p) = (unsafe { CStr::from_ptr(prompt) }).to_str() else { return -1 };
    let Some(lock) = ENGINE.get() else { return -1 };
    let Ok(mut engine) = lock.lock() else { return -1 };
    engine.rewrite_prompt = p.to_string();
    engine.rewrite = None;
    0
}

#[no_mangle]
pub extern "C" fn atypik_correct(input: *const c_char, out_buf: *mut c_char, buf_len: c_int) -> c_int {
    let Ok(text) = (unsafe { CStr::from_ptr(input) }).to_str() else { return -1 };
    let Some(lock) = ENGINE.get() else { return -1 };
    let Ok(mut engine) = lock.lock() else { return -1 };
    let engine: &mut Engine = &mut *engine;
    match run_mode(engine.model, &mut engine.correct, text) {
        Ok(out) => write_cstring(&out, out_buf, buf_len),
        Err(_) => -1,
    }
}

#[no_mangle]
pub extern "C" fn atypik_rewrite(input: *const c_char, out_buf: *mut c_char, buf_len: c_int) -> c_int {
    let Ok(text) = (unsafe { CStr::from_ptr(input) }).to_str() else { return -1 };
    let Some(lock) = ENGINE.get() else { return -1 };
    let Ok(mut engine) = lock.lock() else { return -1 };
    let engine: &mut Engine = &mut *engine;
    if engine.rewrite.is_none() {
        let prompt = engine.rewrite_prompt.clone();
        match build_mode(engine.model, &engine.backend, engine.n_threads, &prompt) {
            Ok(m) => engine.rewrite = Some(m),
            Err(_) => return -1,
        }
    }
    let Some(ref mut mode) = engine.rewrite else { return -1 };
    match run_mode(engine.model, mode, text) {
        Ok(out) => write_cstring(&out, out_buf, buf_len),
        Err(_) => -1,
    }
}

#[no_mangle]
pub extern "C" fn atypik_reset_context() {}

#[no_mangle]
pub extern "C" fn atypik_free() {}

// -- Internals ---------------------------------------------------------------

fn load_engine(path: &str, system_prompt: &str) -> Result<Engine> {
    let backend = LlamaBackend::init()?;
    // Leak the model so contexts can borrow it for 'static; the engine lives for
    // the whole process, and atypik_free is a no-op by design anyway.
    let model: &'static LlamaModel = Box::leak(Box::new(LlamaModel::load_from_file(
        &backend,
        path,
        &LlamaModelParams::default(),
    )?));

    let n_threads = std::thread::available_parallelism()
        .map(|n| n.get() as i32)
        .unwrap_or(4)
        .max(1);

    let correct = build_mode(model, &backend, n_threads, system_prompt)?;
    Ok(Engine {
        backend,
        model,
        n_threads,
        correct,
        rewrite: None,
        rewrite_prompt: REWRITE_SYSTEM_PROMPT.to_string(),
    })
}

fn build_mode(
    model: &'static LlamaModel,
    backend: &LlamaBackend,
    n_threads: i32,
    system: &str,
) -> Result<Mode> {
    let tmpl = model.chat_template(None)?;
    let sys_msg = LlamaChatMessage::new("system".to_string(), system.to_string())?;
    let sys_str = model.apply_chat_template(&tmpl, std::slice::from_ref(&sys_msg), false)?;
    let sys_tokens = model.str_to_token(&sys_str, AddBos::Always)?;

    let params = LlamaContextParams::default()
        .with_n_ctx(Some(NonZeroU32::new(1024).unwrap()))
        .with_n_threads(n_threads);
    let mut ctx = model.new_context(backend, params)?;

    // Evaluate the system prompt once at positions [0, sys_len).
    let n = sys_tokens.len();
    let mut batch = LlamaBatch::new(n, 1);
    for (i, tok) in sys_tokens.iter().enumerate() {
        batch.add(*tok, i as i32, &[0], i == n - 1)?;
    }
    ctx.decode(&mut batch)?;

    Ok(Mode {
        ctx,
        system: system.to_string(),
        sys_tokens,
        sys_len: n as i32,
    })
}

fn run_mode(model: &LlamaModel, mode: &mut Mode, input: &str) -> Result<String> {
    let tmpl = model.chat_template(None)?;
    let sys_msg = LlamaChatMessage::new("system".to_string(), mode.system.clone())?;
    let user_msg = LlamaChatMessage::new("user".to_string(), input.to_string())?;
    let full_str = model.apply_chat_template(&tmpl, &[sys_msg, user_msg], true)?;
    let full_tokens = model.str_to_token(&full_str, AddBos::Always)?;

    let pos0 = mode.sys_len as usize;
    let reuse = full_tokens.len() >= pos0
        && full_tokens[..pos0]
            .iter()
            .map(|t| t.0)
            .eq(mode.sys_tokens.iter().map(|t| t.0));

    // Rewind: drop everything after the system prompt.
    let _ = mode.ctx.clear_kv_cache_seq(Some(0), Some(mode.sys_len as u32), None);

    let start_pos: i32;
    if reuse {
        let suffix = &full_tokens[pos0..];
        let n = suffix.len();
        let mut batch = LlamaBatch::new(n, 1);
        for (i, tok) in suffix.iter().enumerate() {
            batch.add(*tok, mode.sys_len + i as i32, &[0], i == n - 1)?;
        }
        mode.ctx.decode(&mut batch)?;
        start_pos = mode.sys_len + n as i32;
    } else {
        let _ = mode.ctx.clear_kv_cache_seq(Some(0), None, None);
        let n = full_tokens.len();
        let mut batch = LlamaBatch::new(n, 1);
        for (i, tok) in full_tokens.iter().enumerate() {
            batch.add(*tok, i as i32, &[0], i == n - 1)?;
        }
        mode.ctx.decode(&mut batch)?;
        start_pos = n as i32;
    }

    let mut sampler = LlamaSampler::chain_simple([LlamaSampler::temp(0.1), LlamaSampler::greedy()]);
    let mut output = String::new();
    let mut last = start_pos - 1;
    let mut pos = start_pos;
    let mut produced = 0;
    const MAX_NEW: i32 = 256;

    loop {
        let token = sampler.sample(&mode.ctx, last);
        if model.is_eog_token(token) || produced >= MAX_NEW {
            break;
        }
        #[allow(deprecated)]
        if let Ok(piece) = model.token_to_str(token, Special::Tokenize) {
            output.push_str(&piece);
        }
        sampler.accept(token);

        let mut b = LlamaBatch::new(1, 1);
        b.add(token, pos, &[0], true)?;
        mode.ctx.decode(&mut b)?;
        last = pos;
        pos += 1;
        produced += 1;
    }

    Ok(output.trim().to_string())
}

fn write_cstring(s: &str, out_buf: *mut c_char, buf_len: c_int) -> c_int {
    let Ok(cs) = CString::new(s) else { return -1 };
    let bytes = cs.as_bytes_with_nul();
    let len = bytes.len().min(buf_len.max(0) as usize);
    if len == 0 {
        return -1;
    }
    unsafe {
        std::ptr::copy_nonoverlapping(bytes.as_ptr() as *const c_char, out_buf, len);
    }
    len as c_int
}
