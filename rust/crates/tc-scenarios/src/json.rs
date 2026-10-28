//! Минимальный JSON-ридер (RFC 8259) без сторонних крейтов (ADR-0022, rust-pilot.md §4, §6.4).
//!
//! Как `System.Text.Json` с `CommentHandling.Disallow`, `AllowTrailingCommas = false`, `MaxDepth = 64`:
//! без комментариев и висячих запятых, глубина вложенности <= 64, `\u`-суррогаты только парами.
//! Числа хранятся токеном (строкой) и разбираются по требованию: `f64` через `str::parse` (корректное округление),
//! целые - только токены без `.`/`e`/`E`. Ни одна ошибка не паникует.

use std::fmt;

/// Предел вложенности контейнеров (как `JsonReaderOptions.MaxDepth` по умолчанию).
pub const MAX_DEPTH: usize = 64;

#[derive(Clone, Debug, PartialEq)]
pub enum Json {
    Null,
    Bool(bool),
    /// Токен числа как в исходнике (грамматика RFC 8259 уже проверена).
    Num(String),
    Str(String),
    Arr(Vec<Json>),
    /// Поля в порядке документа; дубликаты ключей ридер не отвергает (это делает загрузчик).
    Obj(Vec<(String, Json)>),
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct JsonError {
    /// Смещение в байтах от начала текста.
    pub offset: usize,
    pub msg: &'static str,
}

impl fmt::Display for JsonError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{} (byte {})", self.msg, self.offset)
    }
}

impl std::error::Error for JsonError {}

impl Json {
    /// Токен числа -> `f64` (ближайшее значение, round-to-nearest-even); не число, не конечное -> `None`.
    pub fn as_f64(&self) -> Option<f64> {
        match self {
            Json::Num(t) => t.parse::<f64>().ok().filter(|v| v.is_finite()),
            _ => None,
        }
    }

    /// Как `JsonElement.TryGetInt32`: токен без `.`/`e`/`E`, в диапазоне `i32`.
    pub fn as_i32(&self) -> Option<i32> {
        match self {
            Json::Num(t) if is_integer_token(t) => t.parse::<i32>().ok(),
            _ => None,
        }
    }

    /// Как `JsonElement.TryGetUInt64`: токен без знака минус (кроме `-0`), без `.`/`e`/`E`.
    pub fn as_u64(&self) -> Option<u64> {
        match self {
            Json::Num(t) if is_integer_token(t) => match t.strip_prefix('-') {
                Some("0") => Some(0),
                Some(_) => None,
                None => t.parse::<u64>().ok(),
            },
            _ => None,
        }
    }
}

fn is_integer_token(t: &str) -> bool {
    !t.bytes().any(|b| matches!(b, b'.' | b'e' | b'E'))
}

/// Разбирает весь текст: ровно одно значение, затем только пробельные символы.
pub fn parse(text: &str) -> Result<Json, JsonError> {
    let mut p = Parser { s: text.as_bytes(), i: 0 };
    p.ws();
    let v = p.value(0)?;
    p.ws();
    if p.i != p.s.len() {
        return Err(p.err("unexpected data after the value"));
    }
    Ok(v)
}

struct Parser<'a> {
    s: &'a [u8],
    i: usize,
}

impl Parser<'_> {
    fn err(&self, msg: &'static str) -> JsonError {
        JsonError { offset: self.i, msg }
    }

    fn peek(&self) -> Option<u8> {
        self.s.get(self.i).copied()
    }

    fn ws(&mut self) {
        while matches!(self.peek(), Some(b' ' | b'\t' | b'\n' | b'\r')) {
            self.i += 1;
        }
    }

    fn eat(&mut self, b: u8, msg: &'static str) -> Result<(), JsonError> {
        if self.peek() == Some(b) {
            self.i += 1;
            Ok(())
        } else {
            Err(self.err(msg))
        }
    }

    fn literal(&mut self, word: &'static str, v: Json) -> Result<Json, JsonError> {
        if self.s[self.i..].starts_with(word.as_bytes()) {
            self.i += word.len();
            Ok(v)
        } else {
            Err(self.err("invalid literal"))
        }
    }

    fn value(&mut self, depth: usize) -> Result<Json, JsonError> {
        match self.peek() {
            None => Err(self.err("unexpected end of input")),
            Some(b'{') => self.object(depth + 1),
            Some(b'[') => self.array(depth + 1),
            Some(b'"') => Ok(Json::Str(self.string()?)),
            Some(b't') => self.literal("true", Json::Bool(true)),
            Some(b'f') => self.literal("false", Json::Bool(false)),
            Some(b'n') => self.literal("null", Json::Null),
            Some(b'-' | b'0'..=b'9') => self.number(),
            Some(_) => Err(self.err("unexpected character")),
        }
    }

    fn object(&mut self, depth: usize) -> Result<Json, JsonError> {
        if depth > MAX_DEPTH {
            return Err(self.err("maximum depth exceeded"));
        }
        self.i += 1; // '{'
        let mut fields = Vec::new();
        self.ws();
        if self.peek() == Some(b'}') {
            self.i += 1;
            return Ok(Json::Obj(fields));
        }
        loop {
            self.ws();
            if self.peek() != Some(b'"') {
                return Err(self.err("expected a string key"));
            }
            let key = self.string()?;
            self.ws();
            self.eat(b':', "expected ':'")?;
            self.ws();
            let v = self.value(depth)?;
            fields.push((key, v));
            self.ws();
            match self.peek() {
                Some(b',') => self.i += 1,
                Some(b'}') => {
                    self.i += 1;
                    return Ok(Json::Obj(fields));
                }
                _ => return Err(self.err("expected ',' or '}'")),
            }
        }
    }

    fn array(&mut self, depth: usize) -> Result<Json, JsonError> {
        if depth > MAX_DEPTH {
            return Err(self.err("maximum depth exceeded"));
        }
        self.i += 1; // '['
        let mut items = Vec::new();
        self.ws();
        if self.peek() == Some(b']') {
            self.i += 1;
            return Ok(Json::Arr(items));
        }
        loop {
            self.ws();
            items.push(self.value(depth)?);
            self.ws();
            match self.peek() {
                Some(b',') => self.i += 1,
                Some(b']') => {
                    self.i += 1;
                    return Ok(Json::Arr(items));
                }
                _ => return Err(self.err("expected ',' or ']'")),
            }
        }
    }

    fn digits(&mut self) -> usize {
        let start = self.i;
        while matches!(self.peek(), Some(b'0'..=b'9')) {
            self.i += 1;
        }
        self.i - start
    }

    /// `-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?`
    fn number(&mut self) -> Result<Json, JsonError> {
        let start = self.i;
        if self.peek() == Some(b'-') {
            self.i += 1;
        }
        match self.peek() {
            Some(b'0') => self.i += 1,
            Some(b'1'..=b'9') => {
                self.digits();
            }
            _ => return Err(self.err("invalid number")),
        }
        if self.peek() == Some(b'.') {
            self.i += 1;
            if self.digits() == 0 {
                return Err(self.err("invalid number: digits expected after '.'"));
            }
        }
        if matches!(self.peek(), Some(b'e' | b'E')) {
            self.i += 1;
            if matches!(self.peek(), Some(b'+' | b'-')) {
                self.i += 1;
            }
            if self.digits() == 0 {
                return Err(self.err("invalid number: digits expected in exponent"));
            }
        }
        // Токен состоит только из ASCII, срез по границам символов.
        let tok = std::str::from_utf8(&self.s[start..self.i]).map_err(|_| self.err("invalid number"))?;
        Ok(Json::Num(tok.to_string()))
    }

    fn hex4(&mut self) -> Result<u32, JsonError> {
        let mut v = 0u32;
        for _ in 0..4 {
            let d = match self.peek() {
                Some(b @ b'0'..=b'9') => u32::from(b - b'0'),
                Some(b @ b'a'..=b'f') => u32::from(b - b'a') + 10,
                Some(b @ b'A'..=b'F') => u32::from(b - b'A') + 10,
                _ => return Err(self.err("invalid \\u escape")),
            };
            v = v * 16 + d;
            self.i += 1;
        }
        Ok(v)
    }

    fn string(&mut self) -> Result<String, JsonError> {
        self.i += 1; // открывающая кавычка
        let mut out: Vec<u8> = Vec::new();
        loop {
            let b = match self.peek() {
                None => return Err(self.err("unterminated string")),
                Some(b) => b,
            };
            match b {
                b'"' => {
                    self.i += 1;
                    return String::from_utf8(out).map_err(|_| self.err("invalid UTF-8 in string"));
                }
                0..=0x1F => return Err(self.err("control character in string")),
                b'\\' => {
                    self.i += 1;
                    let e = self.peek().ok_or_else(|| self.err("unterminated escape"))?;
                    self.i += 1;
                    match e {
                        b'"' => out.push(b'"'),
                        b'\\' => out.push(b'\\'),
                        b'/' => out.push(b'/'),
                        b'b' => out.push(0x08),
                        b'f' => out.push(0x0C),
                        b'n' => out.push(b'\n'),
                        b'r' => out.push(b'\r'),
                        b't' => out.push(b'\t'),
                        b'u' => {
                            let c = self.unicode_escape()?;
                            let mut buf = [0u8; 4];
                            out.extend_from_slice(c.encode_utf8(&mut buf).as_bytes());
                        }
                        _ => return Err(self.err("invalid escape")),
                    }
                }
                _ => {
                    out.push(b);
                    self.i += 1;
                }
            }
        }
    }

    /// После `\u`: одно BMP-значение или пара `\uD800..\uDBFF` + `\uDC00..\uDFFF`.
    fn unicode_escape(&mut self) -> Result<char, JsonError> {
        let hi = self.hex4()?;
        let cp = match hi {
            0xD800..=0xDBFF => {
                if !self.s[self.i..].starts_with(b"\\u") {
                    return Err(self.err("high surrogate without a low surrogate"));
                }
                self.i += 2;
                let lo = self.hex4()?;
                if !(0xDC00..=0xDFFF).contains(&lo) {
                    return Err(self.err("invalid low surrogate"));
                }
                0x10000 + ((hi - 0xD800) << 10) + (lo - 0xDC00)
            }
            0xDC00..=0xDFFF => return Err(self.err("lone low surrogate")),
            _ => hi,
        };
        char::from_u32(cp).ok_or_else(|| self.err("invalid code point"))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn num(t: &str) -> Json {
        Json::Num(t.to_string())
    }

    #[test]
    fn parses_scalars_and_containers() {
        assert_eq!(parse(" null ").unwrap(), Json::Null);
        assert_eq!(parse("true").unwrap(), Json::Bool(true));
        assert_eq!(parse("false").unwrap(), Json::Bool(false));
        assert_eq!(parse("[]").unwrap(), Json::Arr(vec![]));
        assert_eq!(parse("{}").unwrap(), Json::Obj(vec![]));
        assert_eq!(
            parse("{\"a\": [1, -2.5e3, \"x\"], \"b\": {}}").unwrap(),
            Json::Obj(vec![
                ("a".into(), Json::Arr(vec![num("1"), num("-2.5e3"), Json::Str("x".into())])),
                ("b".into(), Json::Obj(vec![])),
            ])
        );
    }

    #[test]
    fn string_escapes() {
        let v = parse(r#""a\"b\\c\/d\b\f\n\r\tAé€""#).unwrap();
        assert_eq!(v, Json::Str("a\"b\\c/d\u{8}\u{c}\n\r\tA\u{e9}\u{20ac}".into()));
        // Пара суррогатов -> U+1F600; сырой UTF-8 проходит как есть.
        assert_eq!(parse(r#""😀""#).unwrap(), Json::Str("\u{1F600}".into()));
        assert_eq!(parse("\"п\u{1F600}\"").unwrap(), Json::Str("п\u{1F600}".into()));
        assert_eq!(parse(r#""\u0000""#).unwrap(), Json::Str("\0".into()));
    }

    #[test]
    fn string_errors() {
        for bad in [
            r#""\uD83D""#,  // верхний суррогат без пары
            r#""\uD83Dx""#, // то же
            r#""\uD83DA""#, // вторая половина не суррогат
            r#""\uDE00""#,  // одинокий нижний
            r#""\u12G4""#,
            r#""\u12""#,
            r#""\x""#,
            "\"a\nb\"", // сырой перевод строки
            "\"a\u{1}b\"",
            "\"abc",
            "\"abc\\",
        ] {
            assert!(parse(bad).is_err(), "{bad:?} должно быть ошибкой");
        }
    }

    #[test]
    fn number_grammar() {
        for ok in [
            "0",
            "-0",
            "1",
            "-12",
            "0.5",
            "1e3",
            "1E+3",
            "1e-3",
            "-1.25e10",
            "123456789012345678901234567890",
        ] {
            assert_eq!(parse(ok).unwrap(), num(ok), "{ok}");
        }
        for bad in [
            "", "-", "+1", "01", "-01", "1.", ".5", "1e", "1e+", "0x10", "1.2.3", "--1", "NaN", "Infinity", "1 2",
        ] {
            assert!(parse(bad).is_err(), "{bad:?} должно быть ошибкой");
        }
    }

    #[test]
    fn number_conversion() {
        assert_eq!(num("0.1").as_f64().unwrap().to_bits(), 0.1f64.to_bits());
        // 0.1 + 0.2 - ровно как double.Parse("0.30000000000000004")
        let a = num("0.1").as_f64().unwrap();
        let b = num("0.2").as_f64().unwrap();
        assert_eq!((a + b).to_bits(), 0x3FD3_3333_3333_3334);
        assert_eq!(num("0.30000000000000004").as_f64().unwrap().to_bits(), 0x3FD3_3333_3333_3334);
        // -0 сохраняет знак
        assert_eq!(num("-0").as_f64().unwrap().to_bits(), (-0.0f64).to_bits());
        assert_eq!(num("-0.0").as_f64().unwrap().to_bits(), (-0.0f64).to_bits());
        assert_eq!(num("1e-3").as_f64().unwrap().to_bits(), 0.001f64.to_bits());
        assert_eq!(num("123.456").as_f64().unwrap().to_bits(), 123.456f64.to_bits());
        // 2^53+1 лежит ровно посередине - к чётному
        assert_eq!(
            num("9007199254740993").as_f64().unwrap().to_bits(),
            9_007_199_254_740_992f64.to_bits()
        );
        // Двойное округление f64 -> f32 (контракт §6.4)
        assert_eq!(num("0.1").as_f64().unwrap() as f32, 0.1f32);
        // Бесконечность и переполнение - не число
        assert_eq!(num("1e999").as_f64(), None);
        assert_eq!(Json::Null.as_f64(), None);
    }

    #[test]
    fn integer_conversion() {
        assert_eq!(num("0").as_i32(), Some(0));
        assert_eq!(num("-0").as_i32(), Some(0));
        assert_eq!(num("2147483647").as_i32(), Some(i32::MAX));
        assert_eq!(num("-2147483648").as_i32(), Some(i32::MIN));
        assert_eq!(num("2147483648").as_i32(), None);
        assert_eq!(num("1.0").as_i32(), None);
        assert_eq!(num("1e2").as_i32(), None);
        assert_eq!(num("18446744073709551615").as_u64(), Some(u64::MAX));
        assert_eq!(num("18446744073709551616").as_u64(), None);
        assert_eq!(num("-1").as_u64(), None);
        assert_eq!(num("-0").as_u64(), Some(0));
        assert_eq!(num("5.0").as_u64(), None);
    }

    #[test]
    fn rejects_trailing_commas_comments_and_garbage() {
        for bad in [
            "[1,]",
            "{\"a\":1,}",
            "[,1]",
            "{,}",
            "{\"a\" 1}",
            "{\"a\":}",
            "{1:2}",
            "{'a':1}",
            "[1 2]",
            "// c\n1",
            "/* c */ 1",
            "[1] // c",
            "[1] x",
            "nul",
            "tru",
            "truee",
            "\u{feff}1",
            "",
            "   ",
        ] {
            assert!(parse(bad).is_err(), "{bad:?} должно быть ошибкой");
        }
    }

    #[test]
    fn truncated_input_is_an_error_not_a_panic() {
        let full = "{\"a\": [1, 2.5e-3, \"s\\u00e9\\\"\", {\"b\": null, \"c\": true}], \"d\": false}";
        assert!(parse(full).is_ok());
        for cut in 0..full.len() {
            if !full.is_char_boundary(cut) {
                continue;
            }
            assert!(parse(&full[..cut]).is_err(), "обрезка по {cut} должна быть ошибкой");
        }
    }

    #[test]
    fn depth_limit_is_64() {
        let nest = |n: usize| format!("{}{}", "[".repeat(n), "]".repeat(n));
        assert!(parse(&nest(64)).is_ok());
        assert!(parse(&nest(65)).is_err());
        let obj = |n: usize| format!("{}1{}", "{\"a\":".repeat(n), "}".repeat(n));
        assert!(parse(&obj(64)).is_ok());
        assert!(parse(&obj(65)).is_err());
        // Очень глубокая вложенность не валит стек.
        assert!(parse(&"[".repeat(1_000_000)).is_err());
    }

    #[test]
    fn whitespace_set_is_json_only() {
        assert!(parse("\t\r\n [ 1 ,\n2 ] \n").is_ok());
        assert!(parse("\u{a0}1").is_err());
        assert!(parse("\u{b}1").is_err());
    }

    #[test]
    fn duplicate_keys_are_kept_in_order() {
        assert_eq!(
            parse("{\"a\":1,\"a\":2}").unwrap(),
            Json::Obj(vec![("a".into(), num("1")), ("a".into(), num("2"))])
        );
    }
}
