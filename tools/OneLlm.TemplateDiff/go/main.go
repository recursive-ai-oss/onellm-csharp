// Reference driver for the template differential. It runs Go's text/template with the same function set the C# engine
// provides, so the two can be compared on generated templates and data.
//
// Input: JSON lines {"t": "<template text>", "data": <json>, "missingKey": "default"|"error", "embeds": {"name": "text"}}.
// Output: JSON lines {"ok": true, "out": "..."} or {"ok": false, "stage": "parse"|"exec", "err": "..."}.
package main

import (
	"bufio"
	"bytes"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"text/template"
	"time"
	"unicode/utf8"
)

type testCase struct {
	T          string            `json:"t"`
	Data       json.RawMessage   `json:"data"`
	MissingKey string            `json:"missingKey"`
	Embeds     map[string]string `json:"embeds"`
	Escape     *bool             `json:"escape"`
}

type result struct {
	Ok    bool   `json:"ok"`
	Out   string `json:"out,omitempty"`
	Stage string `json:"stage,omitempty"`
	Err   string `json:"err,omitempty"`
}

// decode reads JSON the way the C# side does: integer syntax is an int, anything with a point or exponent a float64.
func decode(raw json.RawMessage) (interface{}, error) {
	if len(raw) == 0 {
		return nil, nil
	}
	dec := json.NewDecoder(bytes.NewReader(raw))
	dec.UseNumber()
	var v interface{}
	if err := dec.Decode(&v); err != nil {
		return nil, err
	}
	return convert(v), nil
}

func convert(v interface{}) interface{} {
	switch t := v.(type) {
	case json.Number:
		s := string(t)
		if strings.ContainsAny(s, ".eE") {
			f, _ := t.Float64()
			return f
		}
		i, err := t.Int64()
		if err != nil {
			f, _ := t.Float64()
			return f
		}
		return int(i)
	case map[string]interface{}:
		for k, e := range t {
			t[k] = convert(e)
		}
		return t
	case []interface{}:
		for i, e := range t {
			t[i] = convert(e)
		}
		return t
	}
	return v
}

func main() {
	in := bufio.NewReaderSize(os.Stdin, 1<<20)
	out := bufio.NewWriterSize(os.Stdout, 1<<20)
	defer out.Flush()
	enc := json.NewEncoder(out)
	enc.SetEscapeHTML(false)
	for {
		line, err := in.ReadBytes('\n')
		if len(bytes.TrimSpace(line)) > 0 {
			var c testCase
			if jerr := json.Unmarshal(line, &c); jerr != nil {
				enc.Encode(result{Stage: "input", Err: jerr.Error()})
			} else {
				enc.Encode(run(c))
			}
			out.Flush() // the C# side waits for each answer before sending the next case
		}
		if err != nil {
			return
		}
	}
}

func run(c testCase) (res result) {
	defer func() {
		if r := recover(); r != nil {
			res = result{Stage: "exec", Err: fmt.Sprint("panic: ", r)}
		}
	}()
	data, err := decode(c.Data)
	if err != nil {
		return result{Stage: "input", Err: err.Error()}
	}
	escape := c.Escape == nil || *c.Escape
	r := &runner{embeds: c.Embeds, missing: c.MissingKey, data: data, escape: escape}
	s, stage, err := r.render(c.T)
	if err != nil {
		return result{Stage: stage, Err: err.Error()}
	}
	return result{Ok: true, Out: s}
}

type runner struct {
	embeds  map[string]string
	missing string
	data    interface{}
	escape  bool
	stack   []string
}

func (r *runner) render(text string) (string, string, error) {
	if !strings.Contains(text, "{{") {
		return text, "", nil
	}
	if r.escape {
		text = escapeCode(text)
	}
	t := template.New("template").Funcs(r.funcs())
	if r.missing == "error" {
		t = t.Option("missingkey=error")
	}
	t, err := t.Parse(text)
	if err != nil {
		return "", "parse", err
	}
	var buf bytes.Buffer
	if err := t.Execute(&buf, r.data); err != nil {
		return "", "exec", err
	}
	return buf.String(), "", nil
}

func (r *runner) embed(name string) (string, error) {
	if name == "" {
		return "", fmt.Errorf("embed: reference is required")
	}
	body, ok := r.embeds[name]
	if !ok {
		return "", fmt.Errorf("embed %q: not found", name)
	}
	for _, p := range r.stack {
		if p == name {
			return "", fmt.Errorf("embed %q: cycle detected", name)
		}
	}
	if len(r.stack) >= 16 {
		return "", fmt.Errorf("embed %q: too deep", name)
	}
	r.stack = append(r.stack, name)
	defer func() { r.stack = r.stack[:len(r.stack)-1] }()
	if !strings.Contains(body, "{{") {
		return body, nil
	}
	s, _, err := r.render(body)
	if err != nil {
		return "", fmt.Errorf("embed %q: failed to render nested template: %w", name, err)
	}
	return s, nil
}

func (r *runner) funcs() template.FuncMap {
	return template.FuncMap{
		"raw":   func(s string) string { return s },
		"embed": func(uri string) (string, error) { return r.embed(uri) },

		"base":     filepath.Base,
		"dir":      filepath.Dir,
		"ext":      filepath.Ext,
		"trimExt":  func(s string) string { return strings.TrimSuffix(s, filepath.Ext(s)) },
		"pathJoin": func(elem ...string) string { return filepath.Join(elem...) },
		"isAbs":    filepath.IsAbs,

		"lower":      strings.ToLower,
		"upper":      strings.ToUpper,
		"trim":       strings.TrimSpace,
		"trimPrefix": strings.TrimPrefix,
		"trimSuffix": strings.TrimSuffix,
		"replace":    func(s, old, new string) string { return strings.ReplaceAll(s, old, new) },
		"contains":   strings.Contains,
		"hasPrefix":  strings.HasPrefix,
		"hasSuffix":  strings.HasSuffix,
		"default": func(dflt interface{}, val interface{}) interface{} {
			if val == nil {
				return dflt
			}
			if s, ok := val.(string); ok && s == "" {
				return dflt
			}
			return val
		},

		// OneLLM extensions
		"toJson": func(v interface{}) (string, error) {
			var b bytes.Buffer
			e := json.NewEncoder(&b)
			e.SetEscapeHTML(false)
			if err := e.Encode(v); err != nil {
				return "", err
			}
			return strings.TrimRight(b.String(), "\n"), nil
		},
		"toPrettyJson": func(v interface{}) (string, error) {
			var b bytes.Buffer
			e := json.NewEncoder(&b)
			e.SetEscapeHTML(false)
			e.SetIndent("", "  ")
			if err := e.Encode(v); err != nil {
				return "", err
			}
			return strings.TrimRight(b.String(), "\n"), nil
		},
		"join": func(sep string, list interface{}) (string, error) {
			if list == nil {
				return "", nil
			}
			l, ok := list.([]interface{})
			if !ok {
				return "", fmt.Errorf("join needs a list")
			}
			parts := make([]string, len(l))
			for i, e := range l {
				parts[i] = fmt.Sprint(e)
			}
			return strings.Join(parts, sep), nil
		},
		"split": func(sep, s string) []interface{} {
			parts := strings.Split(s, sep)
			out := make([]interface{}, len(parts))
			for i, p := range parts {
				out[i] = p
			}
			return out
		},
		"indent": func(n int, s string) (string, error) {
			if n < 0 || n > 10000 {
				return "", fmt.Errorf("indent: bad width")
			}
			pad := strings.Repeat(" ", n)
			return pad + strings.ReplaceAll(s, "\n", "\n"+pad), nil
		},
		"nindent": func(n int, s string) (string, error) {
			if n < 0 || n > 10000 {
				return "", fmt.Errorf("indent: bad width")
			}
			pad := strings.Repeat(" ", n)
			return "\n" + pad + strings.ReplaceAll(s, "\n", "\n"+pad), nil
		},
		"truncate": func(n int, s string) string {
			if n <= 0 {
				return ""
			}
			if utf8.RuneCountInString(s) <= n {
				return s
			}
			return string([]rune(s)[:n])
		},
		"date": func(layout string, v interface{}) (string, error) {
			s, ok := v.(string)
			if !ok {
				return "", fmt.Errorf("date needs a time string")
			}
			t, err := time.Parse(time.RFC3339, s)
			if err != nil {
				return "", err
			}
			return t.Format(layout), nil
		},
		"regexMatch": func(pattern, s string) (bool, error) {
			re, err := regexp.Compile(pattern)
			if err != nil {
				return false, err
			}
			return re.MatchString(s), nil
		},
		"regexReplace": func(pattern, repl, s string) (string, error) {
			re, err := regexp.Compile(pattern)
			if err != nil {
				return "", err
			}
			return re.ReplaceAllString(s, repl), nil
		},
		"dict": func(kv ...interface{}) (map[string]interface{}, error) {
			if len(kv)%2 != 0 {
				return nil, fmt.Errorf("dict needs pairs")
			}
			m := map[string]interface{}{}
			for i := 0; i < len(kv); i += 2 {
				k, ok := kv[i].(string)
				if !ok {
					return nil, fmt.Errorf("dict keys must be strings")
				}
				m[k] = kv[i+1]
			}
			return m, nil
		},
		"list": func(items ...interface{}) []interface{} { return append([]interface{}{}, items...) },
	}
}

var _ = sort.Strings

// escapeCode escapes {{ and }} inside Markdown code regions (fenced blocks and inline backtick spans) so they render
// literally. Fences open on a line whose first content is three or more backticks or tildes (up to three spaces of
// indent) and close on a line of at least as many of the same character; an inline span closes at a backtick run of the
// same length; an unclosed fence runs to the end of the text.
func escapeCode(text string) string {
	if !strings.Contains(text, "{{") && !strings.Contains(text, "}}") {
		return text
	}
	if !strings.ContainsAny(text, "`~") {
		return text
	}
	var out strings.Builder
	i := 0
	atLineStart := true
	for i < len(text) {
		if atLineStart {
			j := i
			spaces := 0
			for j < len(text) && text[j] == ' ' && spaces < 4 {
				j++
				spaces++
			}
			if spaces < 4 && j < len(text) && (text[j] == '`' || text[j] == '~') {
				fc := text[j]
				fl := 0
				k := j
				for k < len(text) && text[k] == fc {
					fl++
					k++
				}
				if fl >= 3 {
					lineEnd := k
					for lineEnd < len(text) && text[lineEnd] != '\n' {
						lineEnd++
					}
					if lineEnd < len(text) {
						lineEnd++
					}
					out.WriteString(text[i:lineEnd])
					bodyStart := lineEnd
					bodyEnd := bodyStart
					cs, ce := -1, -1
					for bodyEnd < len(text) {
						ls := bodyEnd
						nl := ls
						for nl < len(text) && text[nl] != '\n' {
							nl++
						}
						if closingFence(text, ls, nl, fc, fl) {
							cs = ls
							ce = nl
							if ce < len(text) {
								ce++
							}
							break
						}
						bodyEnd = nl
						if bodyEnd < len(text) {
							bodyEnd++
						}
					}
					lim := len(text)
					if cs >= 0 {
						lim = cs
					}
					writeEsc(&out, text[bodyStart:lim])
					if cs >= 0 {
						out.WriteString(text[cs:ce])
						i = ce
					} else {
						i = len(text)
					}
					atLineStart = true
					continue
				}
			}
		}
		c := text[i]
		if c == '`' {
			run := 0
			j := i
			for j < len(text) && text[j] == '`' {
				run++
				j++
			}
			cs := -1
			k := j
			for k < len(text) {
				if text[k] == '`' {
					rs := k
					rl := 0
					for k < len(text) && text[k] == '`' {
						rl++
						k++
					}
					if rl == run {
						cs = rs
						break
					}
				} else {
					k++
				}
			}
			if cs >= 0 {
				out.WriteString(text[i:j])
				writeEsc(&out, text[j:cs])
				out.WriteString(text[cs : cs+run])
				i = cs + run
				atLineStart = false
				continue
			}
			out.WriteString(text[i:j])
			i = j
			atLineStart = false
			continue
		}
		out.WriteByte(c)
		atLineStart = c == '\n'
		i++
	}
	return out.String()
}

func closingFence(text string, ls, le int, fc byte, open int) bool {
	j := ls
	spaces := 0
	for j < le && text[j] == ' ' && spaces < 4 {
		j++
		spaces++
	}
	if spaces >= 4 {
		return false
	}
	run := 0
	for j < le && text[j] == fc {
		run++
		j++
	}
	if run < open {
		return false
	}
	for j < le {
		if text[j] != ' ' && text[j] != '\t' {
			return false
		}
		j++
	}
	return true
}

func writeEsc(out *strings.Builder, s string) {
	i := 0
	for i < len(s) {
		if i+1 < len(s) && s[i] == '{' && s[i+1] == '{' {
			out.WriteString(`{{"{{"}}`)
			i += 2
			continue
		}
		if i+1 < len(s) && s[i] == '}' && s[i+1] == '}' {
			out.WriteString(`{{"}}"}}`)
			i += 2
			continue
		}
		out.WriteByte(s[i])
		i++
	}
}
