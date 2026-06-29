use std::{
    ffi::{CStr, CString},
    num::NonZeroU32,
    os::raw::{c_char, c_int},
    sync::Mutex,
};

use anyhow::Result;
use llama_cpp_2::{
    context::params::LlamaContextParams,
    llama_backend::LlamaBackend,
    llama_batch::LlamaBatch,
    model::{params::LlamaModelParams, AddBos, LlamaChatMessage, LlamaModel, Special},
    sampling::LlamaSampler,
};
use once_cell::sync::OnceCell;

// -- Engine --------------------------------------------------------------------

struct Engine {
    backend:      LlamaBackend,
    model:        LlamaModel,
    system:       String,
    /// If true, conversation history is accumulated across calls.
    keep_context: bool,
    history:      Mutex<Vec<(String, String)>>, // (user, assistant) pairs
}

static ENGINE: OnceCell<Mutex<Engine>> = OnceCell::new();

// -- Exported C API ------------------------------------------------------------

/// Initialize the engine.
///
/// `model_path`    - path to any GGUF model file
/// `system_prompt` - correction instructions (plain text)
/// `keep_context`  - 0 = stateless (default), 1 = accumulate history
///
/// Returns 0 on success, -1 on error.
#[no_mangle]
pub extern "C" fn atypik_init(
    model_path:    *const c_char,
    system_prompt: *const c_char,
    keep_context:  c_int,
) -> c_int {
    let Ok(path)   = (unsafe { CStr::from_ptr(model_path) }).to_str()    else { return -1 };
    let Ok(prompt) = (unsafe { CStr::from_ptr(system_prompt) }).to_str() else { return -1 };

    match load_engine(path, prompt, keep_context != 0) {
        Ok(engine) => { let _ = ENGINE.set(Mutex::new(engine)); 0 }
        Err(_)     => -1,
    }
}

/// Correct text using the loaded model.
///
/// Returns bytes written (including null terminator), or -1 on error.
#[no_mangle]
pub extern "C" fn atypik_correct(
    input:   *const c_char,
    out_buf: *mut c_char,
    buf_len: c_int,
) -> c_int {
    let Ok(text) = (unsafe { CStr::from_ptr(input) }).to_str() else { return -1 };

    let Some(lock)   = ENGINE.get()   else { return -1 };
    let Ok(engine)   = lock.lock()    else { return -1 };

    match run_correction(&engine, text) {
        Ok(corrected) => {
            let Ok(cs) = CString::new(corrected.as_str()) else { return -1 };
            let bytes  = cs.as_bytes_with_nul();
            let len    = bytes.len().min(buf_len as usize);
            unsafe {
                std::ptr::copy_nonoverlapping(
                    bytes.as_ptr() as *const c_char,
                    out_buf,
                    len,
                );
            }
            len as c_int
        }
        Err(_) => -1,
    }
}

/// Clear accumulated conversation history (only relevant when keep_context = 1).
#[no_mangle]
pub extern "C" fn atypik_reset_context() {
    if let Some(lock) = ENGINE.get() {
        if let Ok(engine) = lock.lock() {
            engine.history.lock().unwrap().clear();
        }
    }
}

/// Rewrite text to remove aggressive or offensive tone.
///
/// Uses the same loaded model but with a built-in tone-smoothing system prompt.
/// Does NOT accumulate history - always stateless.
///
/// Returns bytes written (including null terminator), or -1 on error.
#[no_mangle]
pub extern "C" fn atypik_rewrite(
    input:   *const c_char,
    out_buf: *mut c_char,
    buf_len: c_int,
) -> c_int {
    let Ok(text) = (unsafe { CStr::from_ptr(input) }).to_str() else { return -1 };

    let Some(lock) = ENGINE.get()  else { return -1 };
    let Ok(engine) = lock.lock()   else { return -1 };

    match run_with_system(&engine, REWRITE_SYSTEM_PROMPT, text) {
        Ok(rewritten) => {
            let Ok(cs) = CString::new(rewritten.as_str()) else { return -1 };
            let bytes  = cs.as_bytes_with_nul();
            let len    = bytes.len().min(buf_len as usize);
            unsafe {
                std::ptr::copy_nonoverlapping(
                    bytes.as_ptr() as *const c_char,
                    out_buf,
                    len,
                );
            }
            len as c_int
        }
        Err(_) => -1,
    }
}

/// No-op - memory freed by OS on FreeLibrary.
#[no_mangle]
pub extern "C" fn atypik_free() {}

// -- Prompts -------------------------------------------------------------------

const REWRITE_SYSTEM_PROMPT: &str = "\
You are a tone-smoothing assistant embedded in an accessibility tool. \
The user types text that may contain frustration, aggression, insults, or strong language. \
Rewrite the message to convey the same meaning in a calm, neutral, and professional tone. \
Remove any offensive language, insults, or markers of anger. \
Work in whatever language the input is written in. \
If the input contains no constructive content at all - only insults, swear words, or pure rage \
with no underlying message - output only three asterisks: *** \
Return only the rewritten text or *** - no explanation, no quotes, nothing else.";

// -- Internal ------------------------------------------------------------------

fn load_engine(path: &str, system: &str, keep_context: bool) -> Result<Engine> {
    let backend      = LlamaBackend::init()?;
    let model_params = LlamaModelParams::default();
    let model        = LlamaModel::load_from_file(&backend, path, &model_params)?;

    Ok(Engine {
        backend,
        model,
        system: system.to_owned(),
        keep_context,
        history: Mutex::new(Vec::new()),
    })
}

fn run_correction(engine: &Engine, input: &str) -> Result<String> {
    let result = run_with_system(engine, &engine.system, input)?;

    // Accumulate history when context mode is on
    if engine.keep_context {
        engine.history.lock().unwrap().push((input.to_owned(), result.clone()));
    }

    Ok(result)
}

/// Core inference with an explicit system prompt.
/// History is never accumulated here - callers handle persistence if needed.
fn run_with_system(engine: &Engine, system: &str, input: &str) -> Result<String> {
    let mut messages: Vec<LlamaChatMessage> = Vec::new();
    messages.push(LlamaChatMessage::new("system".to_string(), system.to_owned())?);
    messages.push(LlamaChatMessage::new("user".to_string(),   input.to_owned())?);

    let tmpl   = engine.model.chat_template(None)?;
    let prompt = engine.model.apply_chat_template(&tmpl, &messages, true)?;

    let ctx_params = LlamaContextParams::default()
        .with_n_ctx(Some(NonZeroU32::new(1024).unwrap()))
        .with_n_threads(4);

    let mut ctx  = engine.model.new_context(&engine.backend, ctx_params)?;
    let tokens   = engine.model.str_to_token(&prompt, AddBos::Always)?;

    let mut batch = LlamaBatch::get_one(&tokens)?;
    ctx.decode(&mut batch)?;

    let mut sampler = LlamaSampler::chain_simple([
        LlamaSampler::temp(0.05),
        LlamaSampler::greedy(),
    ]);

    let mut output = String::new();
    let mut pos    = tokens.len() as i32;

    loop {
        let token = sampler.sample(&ctx, pos - 1);

        if engine.model.is_eog_token(token) || (pos - tokens.len() as i32) >= 256 {
            break;
        }

        #[allow(deprecated)]
        if let Ok(piece) = engine.model.token_to_str(token, Special::Tokenize) {
            output.push_str(&piece);
        }

        sampler.accept(token);

        let next = [token];
        let mut next_batch = LlamaBatch::get_one(&next)?;
        ctx.decode(&mut next_batch)?;
        pos += 1;
    }

    Ok(output.trim().to_owned())
}
