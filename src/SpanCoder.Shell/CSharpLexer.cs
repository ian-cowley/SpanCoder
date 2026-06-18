using System;

namespace SpanCoder.Shell
{
    public enum LineState : byte
    {
        Normal = 0,
        InBlockComment = 1,
        InHtmlComment = 2,
        InHtmlTag = 3,
        InJsBlockComment = 4
    }

    public enum TokenType : byte
    {
        Text,
        Keyword,
        Comment,
        String,
        Number,
        Type,
        Method,
        Preprocessor,
        Attribute,
        Tag,
        Selector,
        Property,
        Heading
    }

    public readonly struct Token
    {
        public readonly int Start;
        public readonly int Length;
        public readonly TokenType Type;

        public Token(int start, int length, TokenType type)
        {
            Start = start;
            Length = length;
            Type = type;
        }
    }

    public ref struct CSharpLexer
    {
        private readonly ReadOnlySpan<char> _text;
        private int _index;
        private LineState _state;

        private bool _isNamespaceOrUsingLine;
        private bool _insideAttributeBrackets;
        private int _attributeParamParenthesisDepth;
        private bool _lastBracketsWereAttributes;
        private TokenType _lastNonWsType;
        private int _lastNonWsStart;
        private int _lastNonWsLength;

        public CSharpLexer(ReadOnlySpan<char> text, LineState startState)
        {
            _text = text;
            _index = 0;
            _state = startState;
            _isNamespaceOrUsingLine = false;
            _insideAttributeBrackets = false;
            _attributeParamParenthesisDepth = 0;
            _lastBracketsWereAttributes = false;
            _lastNonWsType = TokenType.Text;
            _lastNonWsStart = -1;
            _lastNonWsLength = 0;
        }

        public static LineState ComputeEndState(ReadOnlySpan<char> text, LineState startState)
        {
            var lexer = new CSharpLexer(text, startState);
            while (lexer.NextToken(out _, out var nextState))
            {
                startState = nextState;
            }
            return startState;
        }

        private bool IsAllWhitespaceBefore(int index)
        {
            for (int i = 0; i < index; i++)
            {
                if (!char.IsWhiteSpace(_text[i]))
                    return false;
            }
            return true;
        }

        public bool NextToken(out Token token, out LineState nextState)
        {
            nextState = _state;
            if (_index >= _text.Length)
            {
                token = default;
                return false;
            }

            int start = _index;

            // Handle block comment state from previous lines
            if (_state == LineState.InBlockComment)
            {
                int endComment = _text.Slice(_index).IndexOf("*/");
                if (endComment >= 0)
                {
                    int len = endComment + 2;
                    _index += len;
                    _state = LineState.Normal;
                    nextState = LineState.Normal;
                    token = new Token(start, len, TokenType.Comment);
                    _lastNonWsType = token.Type;
                    _lastNonWsStart = token.Start;
                    _lastNonWsLength = token.Length;
                    return true;
                }
                else
                {
                    int len = _text.Length - _index;
                    _index = _text.Length;
                    token = new Token(start, len, TokenType.Comment);
                    _lastNonWsType = token.Type;
                    _lastNonWsStart = token.Start;
                    _lastNonWsLength = token.Length;
                    return true;
                }
            }

            char c = _text[_index];

            // Skip whitespace
            if (char.IsWhiteSpace(c))
            {
                while (_index < _text.Length && char.IsWhiteSpace(_text[_index]))
                {
                    _index++;
                }
                token = new Token(start, _index - start, TokenType.Text);
                return true;
            }

            // Check for preprocessor directive.
            // It must start with '#' as the first non-whitespace character on the line.
            if (start == 0 || (start > 0 && IsAllWhitespaceBefore(start)))
            {
                if (_text[_index] == '#')
                {
                    // Scan the rest of the line for comments
                    int preprocessorEnd = _text.Length;
                    for (int i = _index; i < _text.Length; i++)
                    {
                        if (_text[i] == '/' && i + 1 < _text.Length)
                        {
                            if (_text[i + 1] == '/' || _text[i + 1] == '*')
                            {
                                preprocessorEnd = i;
                                break;
                            }
                        }
                    }
                    int len = preprocessorEnd - _index;
                    _index = preprocessorEnd;
                    token = new Token(start, len, TokenType.Preprocessor);
                    _lastNonWsType = token.Type;
                    _lastNonWsStart = token.Start;
                    _lastNonWsLength = token.Length;
                    return true;
                }
            }

            // 1. Comments & Division Operator
            if (c == '/' && _index + 1 < _text.Length)
            {
                char nextChar = _text[_index + 1];
                if (nextChar == '/')
                {
                    // Single line comment
                    int len = _text.Length - _index;
                    _index = _text.Length;
                    token = new Token(start, len, TokenType.Comment);
                    _lastNonWsType = token.Type;
                    _lastNonWsStart = token.Start;
                    _lastNonWsLength = token.Length;
                    return true;
                }
                else if (nextChar == '*')
                {
                    // Block comment start
                    _index += 2;
                    _state = LineState.InBlockComment;
                    nextState = LineState.InBlockComment;
                    
                    int endComment = _text.Slice(_index).IndexOf("*/");
                    if (endComment >= 0)
                    {
                        int len = endComment + 2 + 2;
                        _index += endComment + 2;
                        _state = LineState.Normal;
                        nextState = LineState.Normal;
                        token = new Token(start, len, TokenType.Comment);
                        _lastNonWsType = token.Type;
                        _lastNonWsStart = token.Start;
                        _lastNonWsLength = token.Length;
                        return true;
                    }
                    else
                    {
                        int len = _text.Length - start;
                        _index = _text.Length;
                        token = new Token(start, len, TokenType.Comment);
                        _lastNonWsType = token.Type;
                        _lastNonWsStart = token.Start;
                        _lastNonWsLength = token.Length;
                        return true;
                    }
                }
            }

            // 2. String Literals
            if (c == '"')
            {
                _index++; // skip opening quote
                bool escaped = false;
                while (_index < _text.Length)
                {
                    char sc = _text[_index];
                    if (sc == '\\' && !escaped)
                    {
                        escaped = true;
                    }
                    else if (sc == '"' && !escaped)
                    {
                        _index++;
                        break;
                    }
                    else
                    {
                        escaped = false;
                    }
                    _index++;
                }
                int len = _index - start;
                token = new Token(start, len, TokenType.String);
                _lastNonWsType = token.Type;
                _lastNonWsStart = token.Start;
                _lastNonWsLength = token.Length;
                return true;
            }

            // 3. Char Literals
            if (c == '\'')
            {
                _index++; // skip opening quote
                bool escaped = false;
                while (_index < _text.Length)
                {
                    char sc = _text[_index];
                    if (sc == '\\' && !escaped)
                    {
                        escaped = true;
                    }
                    else if (sc == '\'' && !escaped)
                    {
                        _index++;
                        break;
                    }
                    else
                    {
                        escaped = false;
                    }
                    _index++;
                }
                int len = _index - start;
                token = new Token(start, len, TokenType.String);
                _lastNonWsType = token.Type;
                _lastNonWsStart = token.Start;
                _lastNonWsLength = token.Length;
                return true;
            }

            // 4. Numeric Literals
            if (char.IsDigit(c))
            {
                while (_index < _text.Length && (char.IsLetterOrDigit(_text[_index]) || _text[_index] == '.'))
                {
                    _index++;
                }
                token = new Token(start, _index - start, TokenType.Number);
                _lastNonWsType = token.Type;
                _lastNonWsStart = token.Start;
                _lastNonWsLength = token.Length;
                return true;
            }

            // 5. Identifiers & Keywords
            if (char.IsLetter(c) || c == '_')
            {
                while (_index < _text.Length && (char.IsLetterOrDigit(_text[_index]) || _text[_index] == '_'))
                {
                    _index++;
                }
                int len = _index - start;
                var word = _text.Slice(start, len);
                
                TokenType type = IsKeyword(word) ? TokenType.Keyword : TokenType.Text;
                if (type == TokenType.Keyword)
                {
                    if (word.SequenceEqual("namespace"))
                    {
                        _isNamespaceOrUsingLine = true;
                    }
                    else if (word.SequenceEqual("using"))
                    {
                        // Check lookahead to differentiate between using statement and using directive
                        int lookAheadIdx = _index;
                        while (lookAheadIdx < _text.Length && char.IsWhiteSpace(_text[lookAheadIdx]))
                        {
                            lookAheadIdx++;
                        }
                        if (lookAheadIdx < _text.Length && _text[lookAheadIdx] != '(')
                        {
                            _isNamespaceOrUsingLine = true;
                        }
                    }
                }
                else
                {
                    // Advanced lookahead classification
                    if (!_isNamespaceOrUsingLine)
                    {
                        int lookAheadIdx = _index;
                        while (lookAheadIdx < _text.Length && char.IsWhiteSpace(_text[lookAheadIdx]))
                        {
                            lookAheadIdx++;
                        }

                        char nextNonWsChar = lookAheadIdx < _text.Length ? _text[lookAheadIdx] : '\0';

                        if (_insideAttributeBrackets && _attributeParamParenthesisDepth == 0)
                        {
                            type = TokenType.Attribute;
                        }
                        else if (nextNonWsChar == '(')
                        {
                            type = TokenType.Method;
                        }
                        else if (nextNonWsChar == '=' || 
                                 (lookAheadIdx + 1 < _text.Length && _text[lookAheadIdx + 1] == '=' && 
                                  (nextNonWsChar == '+' || nextNonWsChar == '-' || nextNonWsChar == '*' || nextNonWsChar == '/')))
                        {
                            type = TokenType.Text;
                        }
                        else
                        {
                            // Preceded by type-defining keyword
                            ReadOnlySpan<char> lastWord = (_lastNonWsStart >= 0) ? _text.Slice(_lastNonWsStart, _lastNonWsLength) : ReadOnlySpan<char>.Empty;
                            bool isPrecededByTypeKeyword = lastWord.SequenceEqual("class") ||
                                                           lastWord.SequenceEqual("struct") ||
                                                           lastWord.SequenceEqual("interface") ||
                                                           lastWord.SequenceEqual("enum") ||
                                                           lastWord.SequenceEqual("delegate") ||
                                                           lastWord.SequenceEqual("new") ||
                                                           lastWord.SequenceEqual("is") ||
                                                           lastWord.SequenceEqual("as") ||
                                                           lastWord.SequenceEqual("typeof");

                            if (isPrecededByTypeKeyword)
                            {
                                type = TokenType.Type;
                            }
                            else if (word.Length > 1 && word[0] == 'I' && char.IsUpper(word[1]))
                            {
                                type = TokenType.Type;
                            }
                            else if (char.IsUpper(word[0]))
                            {
                                bool isPrecededByDot = (_lastNonWsStart >= 0 && _lastNonWsLength == 1 && _text[_lastNonWsStart] == '.');
                                if (!isPrecededByDot)
                                {
                                    type = TokenType.Type;
                                }
                            }

                            // Variable declaration pattern: check if followed by another identifier (possibly with nullable/arrays/generics)
                            if (type == TokenType.Text)
                            {
                                int tempIdx = lookAheadIdx;
                                // Skip '?'
                                if (tempIdx < _text.Length && _text[tempIdx] == '?')
                                {
                                    tempIdx++;
                                    while (tempIdx < _text.Length && char.IsWhiteSpace(_text[tempIdx])) tempIdx++;
                                }
                                // Skip generic parameters <...>
                                if (tempIdx < _text.Length && _text[tempIdx] == '<')
                                {
                                    int depth = 1;
                                    tempIdx++;
                                    while (tempIdx < _text.Length && depth > 0)
                                    {
                                        if (_text[tempIdx] == '<') depth++;
                                        else if (_text[tempIdx] == '>') depth--;
                                        tempIdx++;
                                    }
                                    while (tempIdx < _text.Length && char.IsWhiteSpace(_text[tempIdx])) tempIdx++;
                                }
                                // Skip array brackets []
                                while (tempIdx < _text.Length && _text[tempIdx] == '[')
                                {
                                    tempIdx++;
                                    while (tempIdx < _text.Length && char.IsWhiteSpace(_text[tempIdx])) tempIdx++;
                                    if (tempIdx < _text.Length && _text[tempIdx] == ']')
                                    {
                                        tempIdx++;
                                        while (tempIdx < _text.Length && char.IsWhiteSpace(_text[tempIdx])) tempIdx++;
                                    }
                                    else
                                    {
                                        break; // Incomplete array bracket
                                    }
                                }
                                // Check if there is an identifier next
                                if (tempIdx < _text.Length && (char.IsLetter(_text[tempIdx]) || _text[tempIdx] == '_'))
                                {
                                    int nextWordStart = tempIdx;
                                    while (tempIdx < _text.Length && (char.IsLetterOrDigit(_text[tempIdx]) || _text[tempIdx] == '_'))
                                    {
                                        tempIdx++;
                                    }
                                    var nextWord = _text.Slice(nextWordStart, tempIdx - nextWordStart);
                                    if (!IsKeyword(nextWord))
                                    {
                                        type = TokenType.Type;
                                    }
                                }
                            }
                        }
                    }
                }

                token = new Token(start, len, type);
                if (!_insideAttributeBrackets)
                {
                    _lastBracketsWereAttributes = false;
                }
                _lastNonWsType = token.Type;
                _lastNonWsStart = token.Start;
                _lastNonWsLength = token.Length;
                return true;
            }

            // 6. Symbols
            char symbolChar = _text[_index];
            if (symbolChar == '[')
            {
                bool isArrayIndex = (_lastNonWsStart >= 0 && 
                                     (char.IsLetterOrDigit(_text[_lastNonWsStart]) || _text[_lastNonWsStart] == '_') &&
                                     !_lastBracketsWereAttributes);
                if (!isArrayIndex)
                {
                    _insideAttributeBrackets = true;
                    _attributeParamParenthesisDepth = 0;
                    _lastBracketsWereAttributes = true;
                }
            }
            else if (symbolChar == ']')
            {
                _insideAttributeBrackets = false;
            }
            else if (symbolChar == '(' && _insideAttributeBrackets)
            {
                _attributeParamParenthesisDepth++;
            }
            else if (symbolChar == ')' && _insideAttributeBrackets)
            {
                _attributeParamParenthesisDepth = Math.Max(0, _attributeParamParenthesisDepth - 1);
            }

            _index++;
            token = new Token(start, 1, TokenType.Text);
            if (!_insideAttributeBrackets && symbolChar != '[' && symbolChar != ']')
            {
                _lastBracketsWereAttributes = false;
            }
            _lastNonWsType = token.Type;
            _lastNonWsStart = token.Start;
            _lastNonWsLength = token.Length;
            return true;
        }

        private static bool IsKeyword(ReadOnlySpan<char> word)
        {
            switch (word.Length)
            {
                case 2:
                    return word.SequenceEqual("if") || word.SequenceEqual("in") || word.SequenceEqual("is") || word.SequenceEqual("as") || word.SequenceEqual("do");
                case 3:
                    return word.SequenceEqual("for") || word.SequenceEqual("new") || word.SequenceEqual("int") || word.SequenceEqual("out") || word.SequenceEqual("ref") || word.SequenceEqual("try") || word.SequenceEqual("var");
                case 4:
                    return word.SequenceEqual("void") || word.SequenceEqual("else") || word.SequenceEqual("this") || word.SequenceEqual("long") || word.SequenceEqual("bool") || word.SequenceEqual("char") || word.SequenceEqual("case") || word.SequenceEqual("byte") || word.SequenceEqual("lock") || word.SequenceEqual("true") || word.SequenceEqual("null") || word.SequenceEqual("enum") || word.SequenceEqual("goto") || word.SequenceEqual("base") || word.SequenceEqual("uint");
                case 5:
                    return word.SequenceEqual("class") || word.SequenceEqual("using") || word.SequenceEqual("break") || word.SequenceEqual("float") || word.SequenceEqual("const") || word.SequenceEqual("throw") || word.SequenceEqual("catch") || word.SequenceEqual("while") || word.SequenceEqual("false") || word.SequenceEqual("sbyte") || word.SequenceEqual("short");
                case 6:
                    return word.SequenceEqual("struct") || word.SequenceEqual("public") || word.SequenceEqual("double") || word.SequenceEqual("static") || word.SequenceEqual("return") || word.SequenceEqual("switch") || word.SequenceEqual("string") || word.SequenceEqual("typeof") || word.SequenceEqual("object") || word.SequenceEqual("ushort") || word.SequenceEqual("ulong");
                case 7:
                    return word.SequenceEqual("private") || word.SequenceEqual("partial") || word.SequenceEqual("decimal") || word.SequenceEqual("finally") || word.SequenceEqual("default");
                case 8:
                    return word.SequenceEqual("readonly") || word.SequenceEqual("override") || word.SequenceEqual("internal") || word.SequenceEqual("delegate");
                case 9:
                    return word.SequenceEqual("namespace") || word.SequenceEqual("interface") || word.SequenceEqual("protected");
                default:
                    return false;
            }
        }
    }

    public ref struct PlainLexer
    {
        private readonly ReadOnlySpan<char> _text;
        private int _index;

        public PlainLexer(ReadOnlySpan<char> text)
        {
            _text = text;
            _index = 0;
        }

        public bool NextToken(out Token token)
        {
            if (_index >= _text.Length)
            {
                token = default;
                return false;
            }
            int start = _index;
            _index = _text.Length;
            token = new Token(start, _index - start, TokenType.Text);
            return true;
        }
    }

    public ref struct JsonLexer
    {
        private readonly ReadOnlySpan<char> _text;
        private int _index;

        public JsonLexer(ReadOnlySpan<char> text)
        {
            _text = text;
            _index = 0;
        }

        public bool NextToken(out Token token)
        {
            if (_index >= _text.Length)
            {
                token = default;
                return false;
            }

            int start = _index;
            char c = _text[_index];

            // Skip whitespace
            if (char.IsWhiteSpace(c))
            {
                while (_index < _text.Length && char.IsWhiteSpace(_text[_index]))
                {
                    _index++;
                }
                token = new Token(start, _index - start, TokenType.Text);
                return true;
            }

            // Comments
            if (c == '/' && _index + 1 < _text.Length)
            {
                char nextChar = _text[_index + 1];
                if (nextChar == '/')
                {
                    int len = _text.Length - _index;
                    _index = _text.Length;
                    token = new Token(start, len, TokenType.Comment);
                    return true;
                }
                else if (nextChar == '*')
                {
                    _index += 2;
                    int endComment = _text.Slice(_index).IndexOf("*/");
                    if (endComment >= 0)
                    {
                        int len = endComment + 4;
                        _index += endComment + 2;
                        token = new Token(start, len, TokenType.Comment);
                        return true;
                    }
                    else
                    {
                        int len = _text.Length - start;
                        _index = _text.Length;
                        token = new Token(start, len, TokenType.Comment);
                        return true;
                    }
                }
            }

            // Strings
            if (c == '"')
            {
                _index++; // skip opening quote
                bool escaped = false;
                while (_index < _text.Length)
                {
                    char sc = _text[_index];
                    if (sc == '\\' && !escaped)
                    {
                        escaped = true;
                    }
                    else if (sc == '"' && !escaped)
                    {
                        _index++;
                        break;
                    }
                    else
                    {
                        escaped = false;
                    }
                    _index++;
                }
                int len = _index - start;

                // Lookahead to see if it is a property key (followed by ':')
                int tempIdx = _index;
                while (tempIdx < _text.Length && char.IsWhiteSpace(_text[tempIdx]))
                {
                    tempIdx++;
                }
                bool isKey = tempIdx < _text.Length && _text[tempIdx] == ':';

                token = new Token(start, len, isKey ? TokenType.Property : TokenType.String);
                return true;
            }

            // Numbers
            if (char.IsDigit(c) || c == '-')
            {
                while (_index < _text.Length && (char.IsLetterOrDigit(_text[_index]) || _text[_index] == '.' || _text[_index] == '+' || _text[_index] == '-'))
                {
                    _index++;
                }
                token = new Token(start, _index - start, TokenType.Number);
                return true;
            }

            // Keywords: true, false, null
            if (char.IsLetter(c))
            {
                while (_index < _text.Length && char.IsLetter(_text[_index]))
                {
                    _index++;
                }
                int len = _index - start;
                var word = _text.Slice(start, len);
                bool isKeyword = word.SequenceEqual("true") || word.SequenceEqual("false") || word.SequenceEqual("null");
                token = new Token(start, len, isKeyword ? TokenType.Keyword : TokenType.Text);
                return true;
            }

            // Symbols
            _index++;
            token = new Token(start, 1, TokenType.Text);
            return true;
        }
    }

    public ref struct HtmlLexer
    {
        private readonly ReadOnlySpan<char> _text;
        private int _index;
        private LineState _state;
        private bool _parsedTagNameOnThisLine;

        public HtmlLexer(ReadOnlySpan<char> text, LineState startState)
        {
            _text = text;
            _index = 0;
            _state = startState;
            _parsedTagNameOnThisLine = false;
        }

        public bool NextToken(out Token token, out LineState nextState)
        {
            nextState = _state;
            if (_index >= _text.Length)
            {
                token = default;
                return false;
            }

            int start = _index;

            // 1. Multi-line HTML comment state
            if (_state == LineState.InHtmlComment)
            {
                int endIdx = _text.Slice(_index).IndexOf("-->");
                if (endIdx >= 0)
                {
                    int len = endIdx + 3;
                    _index += len;
                    _state = LineState.Normal;
                    nextState = LineState.Normal;
                    token = new Token(start, len, TokenType.Comment);
                    return true;
                }
                else
                {
                    int len = _text.Length - _index;
                    _index = _text.Length;
                    token = new Token(start, len, TokenType.Comment);
                    return true;
                }
            }

            char c = _text[_index];

            // 2. In HTML Tag state
            if (_state == LineState.InHtmlTag)
            {
                // Skip whitespace
                if (char.IsWhiteSpace(c))
                {
                    while (_index < _text.Length && char.IsWhiteSpace(_text[_index]))
                    {
                        _index++;
                    }
                    token = new Token(start, _index - start, TokenType.Text);
                    return true;
                }

                if (c == '>')
                {
                    _index++;
                    _state = LineState.Normal;
                    nextState = LineState.Normal;
                    token = new Token(start, 1, TokenType.Text);
                    return true;
                }

                if (c == '/' && _index + 1 < _text.Length && _text[_index + 1] == '>')
                {
                    _index += 2;
                    _state = LineState.Normal;
                    nextState = LineState.Normal;
                    token = new Token(start, 2, TokenType.Text);
                    return true;
                }

                // Attribute values (strings)
                if (c == '"' || c == '\'')
                {
                    char quote = c;
                    _index++;
                    while (_index < _text.Length && _text[_index] != quote)
                    {
                        _index++;
                    }
                    if (_index < _text.Length) _index++; // skip closing quote
                    token = new Token(start, _index - start, TokenType.String);
                    return true;
                }

                // Identifiers (Tag name or Attribute names)
                if (char.IsLetter(c) || c == '-' || c == ':')
                {
                    while (_index < _text.Length && (char.IsLetterOrDigit(_text[_index]) || _text[_index] == '-' || _text[_index] == ':'))
                    {
                        _index++;
                    }
                    int len = _index - start;
                    
                    bool isTagName = !_parsedTagNameOnThisLine && (start == 0 || _text[start - 1] == '<' || _text[start - 1] == '/' || (start > 1 && _text[start - 1] == ' ' && (_text[start - 2] == '<' || _text[start - 2] == '/')));
                    
                    if (isTagName)
                    {
                        _parsedTagNameOnThisLine = true;
                        token = new Token(start, len, TokenType.Tag);
                    }
                    else
                    {
                        token = new Token(start, len, TokenType.Property); // HTML Attribute
                    }
                    return true;
                }

                _index++;
                token = new Token(start, 1, TokenType.Text);
                return true;
            }

            // 3. Normal State (plain text or look for '<')
            if (c == '<')
            {
                // Check for comment
                if (_index + 3 < _text.Length && _text.Slice(_index, 4).SequenceEqual("<!--"))
                {
                    _index += 4;
                    _state = LineState.InHtmlComment;
                    nextState = LineState.InHtmlComment;
                    
                    int endIdx = _text.Slice(_index).IndexOf("-->");
                    if (endIdx >= 0)
                    {
                        int len = endIdx + 4 + 3;
                        _index += endIdx + 3;
                        _state = LineState.Normal;
                        nextState = LineState.Normal;
                        token = new Token(start, len, TokenType.Comment);
                        return true;
                    }
                    else
                    {
                        int len = _text.Length - start;
                        _index = _text.Length;
                        token = new Token(start, len, TokenType.Comment);
                        return true;
                    }
                }

                // Tag start
                _index++;
                if (_index < _text.Length && _text[_index] == '/')
                {
                    _index++;
                }
                _state = LineState.InHtmlTag;
                nextState = LineState.InHtmlTag;
                _parsedTagNameOnThisLine = false;
                token = new Token(start, _index - start, TokenType.Text); // the '<' or '</'
                return true;
            }

            // Normal plain text
            while (_index < _text.Length && _text[_index] != '<')
            {
                _index++;
            }
            token = new Token(start, _index - start, TokenType.Text);
            return true;
        }
    }

    public ref struct CssLexer
    {
        private readonly ReadOnlySpan<char> _text;
        private int _index;
        private bool _insideRuleBlock;

        public CssLexer(ReadOnlySpan<char> text)
        {
            _text = text;
            _index = 0;
            _insideRuleBlock = false;
        }

        public bool NextToken(out Token token)
        {
            if (_index >= _text.Length)
            {
                token = default;
                return false;
            }

            int start = _index;
            char c = _text[_index];

            // Skip whitespace
            if (char.IsWhiteSpace(c))
            {
                while (_index < _text.Length && char.IsWhiteSpace(_text[_index]))
                {
                    _index++;
                }
                token = new Token(start, _index - start, TokenType.Text);
                return true;
            }

            // Comments
            if (c == '/' && _index + 1 < _text.Length && _text[_index + 1] == '*')
            {
                _index += 2;
                int endComment = _text.Slice(_index).IndexOf("*/");
                if (endComment >= 0)
                {
                    int len = endComment + 4;
                    _index += endComment + 2;
                    token = new Token(start, len, TokenType.Comment);
                    return true;
                }
                else
                {
                    int len = _text.Length - start;
                    _index = _text.Length;
                    token = new Token(start, len, TokenType.Comment);
                    return true;
                }
            }

            // Braces
            if (c == '{')
            {
                _insideRuleBlock = true;
                _index++;
                token = new Token(start, 1, TokenType.Text);
                return true;
            }
            if (c == '}')
            {
                _insideRuleBlock = false;
                _index++;
                token = new Token(start, 1, TokenType.Text);
                return true;
            }

            // String literals
            if (c == '"' || c == '\'')
            {
                char quote = c;
                _index++;
                while (_index < _text.Length && _text[_index] != quote)
                {
                    _index++;
                }
                if (_index < _text.Length) _index++;
                token = new Token(start, _index - start, TokenType.String);
                return true;
            }

            // Selector or Property / Value
            if (_insideRuleBlock)
            {
                // Numbers
                if (char.IsDigit(c) || c == '#') // hex colors or measurements
                {
                    _index++;
                    while (_index < _text.Length && (char.IsLetterOrDigit(_text[_index]) || _text[_index] == '.' || _text[_index] == '-' || _text[_index] == '%'))
                    {
                        _index++;
                    }
                    token = new Token(start, _index - start, TokenType.Number);
                    return true;
                }

                // Identifiers (Property name or Value name)
                if (char.IsLetter(c) || c == '-')
                {
                    while (_index < _text.Length && (char.IsLetterOrDigit(_text[_index]) || _text[_index] == '-'))
                    {
                        _index++;
                    }
                    int len = _index - start;

                    // Lookahead: is it followed by ':' or '('?
                    int tempIdx = _index;
                    while (tempIdx < _text.Length && char.IsWhiteSpace(_text[tempIdx]))
                    {
                        tempIdx++;
                    }
                    bool isProperty = tempIdx < _text.Length && _text[tempIdx] == ':';
                    bool isFunction = tempIdx < _text.Length && _text[tempIdx] == '(';

                    token = new Token(start, len, isProperty ? TokenType.Property : (isFunction ? TokenType.Method : TokenType.Text));
                    return true;
                }
            }
            else
            {
                // CSS Selector Mode
                while (_index < _text.Length && 
                       _text[_index] != '{' && 
                       _text[_index] != '}' && 
                       _text[_index] != '/' && 
                       _text[_index] != ',' && 
                       _text[_index] != '(' && 
                       _text[_index] != ')' && 
                       _text[_index] != '"' && 
                       _text[_index] != '\'' && 
                       !char.IsWhiteSpace(_text[_index]))
                {
                    _index++;
                }
                int len = _index - start;
                if (len > 0)
                {
                    token = new Token(start, len, TokenType.Selector);
                    return true;
                }
            }

            _index++;
            token = new Token(start, 1, TokenType.Text);
            return true;
        }
    }

    public ref struct JavaScriptLexer
    {
        private readonly ReadOnlySpan<char> _text;
        private int _index;
        private LineState _state;

        public JavaScriptLexer(ReadOnlySpan<char> text, LineState startState)
        {
            _text = text;
            _index = 0;
            _state = startState;
        }

        public bool NextToken(out Token token, out LineState nextState)
        {
            nextState = _state;
            if (_index >= _text.Length)
            {
                token = default;
                return false;
            }

            int start = _index;

            // Handle block comment state from previous lines
            if (_state == LineState.InJsBlockComment || _state == LineState.InBlockComment)
            {
                int endComment = _text.Slice(_index).IndexOf("*/");
                if (endComment >= 0)
                {
                    int len = endComment + 2;
                    _index += len;
                    _state = LineState.Normal;
                    nextState = LineState.Normal;
                    token = new Token(start, len, TokenType.Comment);
                    return true;
                }
                else
                {
                    int len = _text.Length - _index;
                    _index = _text.Length;
                    token = new Token(start, len, TokenType.Comment);
                    return true;
                }
            }

            char c = _text[_index];

            // Skip whitespace
            if (char.IsWhiteSpace(c))
            {
                while (_index < _text.Length && char.IsWhiteSpace(_text[_index]))
                {
                    _index++;
                }
                token = new Token(start, _index - start, TokenType.Text);
                return true;
            }

            // 1. Comments & Division Operator
            if (c == '/' && _index + 1 < _text.Length)
            {
                char nextChar = _text[_index + 1];
                if (nextChar == '/')
                {
                    int len = _text.Length - _index;
                    _index = _text.Length;
                    token = new Token(start, len, TokenType.Comment);
                    return true;
                }
                else if (nextChar == '*')
                {
                    _index += 2;
                    _state = LineState.InJsBlockComment;
                    nextState = LineState.InJsBlockComment;
                    
                    int endComment = _text.Slice(_index).IndexOf("*/");
                    if (endComment >= 0)
                    {
                        int len = endComment + 4;
                        _index += endComment + 2;
                        _state = LineState.Normal;
                        nextState = LineState.Normal;
                        token = new Token(start, len, TokenType.Comment);
                        return true;
                    }
                    else
                    {
                        int len = _text.Length - start;
                        _index = _text.Length;
                        token = new Token(start, len, TokenType.Comment);
                        return true;
                    }
                }
            }

            // 2. String Literals
            if (c == '"' || c == '\'' || c == '`')
            {
                char quote = c;
                _index++;
                bool escaped = false;
                while (_index < _text.Length)
                {
                    char sc = _text[_index];
                    if (sc == '\\' && !escaped)
                    {
                        escaped = true;
                    }
                    else if (sc == quote && !escaped)
                    {
                        _index++;
                        break;
                    }
                    else
                    {
                        escaped = false;
                    }
                    _index++;
                }
                int len = _index - start;
                token = new Token(start, len, TokenType.String);
                return true;
            }

            // 3. Numeric Literals
            if (char.IsDigit(c))
            {
                while (_index < _text.Length && (char.IsLetterOrDigit(_text[_index]) || _text[_index] == '.'))
                {
                    _index++;
                }
                token = new Token(start, _index - start, TokenType.Number);
                return true;
            }

            // 4. Identifiers & Keywords
            if (char.IsLetter(c) || c == '_' || c == '$')
            {
                while (_index < _text.Length && (char.IsLetterOrDigit(_text[_index]) || _text[_index] == '_' || _text[_index] == '$'))
                {
                    _index++;
                }
                int len = _index - start;
                var word = _text.Slice(start, len);
                
                TokenType type = IsJsKeyword(word) ? TokenType.Keyword : TokenType.Text;
                if (type == TokenType.Text)
                {
                    // Lookahead: is it followed by '('?
                    int lookAheadIdx = _index;
                    while (lookAheadIdx < _text.Length && char.IsWhiteSpace(_text[lookAheadIdx]))
                    {
                        lookAheadIdx++;
                    }
                    if (lookAheadIdx < _text.Length && _text[lookAheadIdx] == '(')
                    {
                        type = TokenType.Method;
                    }
                    else if (char.IsUpper(word[0]))
                    {
                        type = TokenType.Type; // PascalCase class names in JS
                    }
                }

                token = new Token(start, len, type);
                return true;
            }

            // 5. Symbols
            _index++;
            token = new Token(start, 1, TokenType.Text);
            return true;
        }

        private static bool IsJsKeyword(ReadOnlySpan<char> word)
        {
            switch (word.Length)
            {
                case 2:
                    return word.SequenceEqual("if") || word.SequenceEqual("in") || word.SequenceEqual("do") || word.SequenceEqual("as") || word.SequenceEqual("of");
                case 3:
                    return word.SequenceEqual("for") || word.SequenceEqual("new") || word.SequenceEqual("let") || word.SequenceEqual("var") || word.SequenceEqual("try") || word.SequenceEqual("use");
                case 4:
                    return word.SequenceEqual("void") || word.SequenceEqual("else") || word.SequenceEqual("this") || word.SequenceEqual("case") || word.SequenceEqual("with") || word.SequenceEqual("true") || word.SequenceEqual("null") || word.SequenceEqual("from");
                case 5:
                    return word.SequenceEqual("class") || word.SequenceEqual("break") || word.SequenceEqual("const") || word.SequenceEqual("throw") || word.SequenceEqual("catch") || word.SequenceEqual("while") || word.SequenceEqual("false") || word.SequenceEqual("yield") || word.SequenceEqual("async") || word.SequenceEqual("await");
                case 6:
                    return word.SequenceEqual("public") || word.SequenceEqual("static") || word.SequenceEqual("return") || word.SequenceEqual("switch") || word.SequenceEqual("typeof") || word.SequenceEqual("import") || word.SequenceEqual("export");
                case 7:
                    return word.SequenceEqual("private") || word.SequenceEqual("finally") || word.SequenceEqual("default") || word.SequenceEqual("extends");
                case 8:
                    return word.SequenceEqual("function") || word.SequenceEqual("debugger");
                case 9:
                    return word.SequenceEqual("interface") || word.SequenceEqual("protected");
                default:
                    return false;
            }
        }
    }

    public ref struct MarkdownLexer
    {
        private readonly ReadOnlySpan<char> _text;
        private int _index;

        public MarkdownLexer(ReadOnlySpan<char> text)
        {
            _text = text;
            _index = 0;
        }

        public bool NextToken(out Token token)
        {
            if (_index >= _text.Length)
            {
                token = default;
                return false;
            }

            int start = _index;

            // 1. Heading check at start of line
            if (start == 0)
            {
                // Skip leading space
                int temp = 0;
                while (temp < _text.Length && _text[temp] == ' ') temp++;
                if (temp < _text.Length && _text[temp] == '#')
                {
                    _index = _text.Length;
                    token = new Token(start, _index - start, TokenType.Heading);
                    return true;
                }
                if (temp + 2 < _text.Length && _text.Slice(temp, 3).SequenceEqual("```"))
                {
                    _index = _text.Length;
                    token = new Token(start, _index - start, TokenType.Preprocessor);
                    return true;
                }
                if (temp < _text.Length && _text[temp] == '>')
                {
                    _index = _text.Length;
                    token = new Token(start, _index - start, TokenType.Comment);
                    return true;
                }
            }

            char c = _text[_index];

            // 2. Inline Code block `...`
            if (c == '`')
            {
                _index++;
                while (_index < _text.Length && _text[_index] != '`')
                {
                    _index++;
                }
                if (_index < _text.Length) _index++;
                token = new Token(start, _index - start, TokenType.Preprocessor);
                return true;
            }

            // 3. Link [text](url)
            if (c == '[')
            {
                _index++;
                int closeBracket = _text.Slice(_index).IndexOf(']');
                if (closeBracket >= 0)
                {
                    int linkStart = _index + closeBracket + 1;
                    if (linkStart < _text.Length && _text[linkStart] == '(')
                    {
                        int closeParen = _text.Slice(linkStart).IndexOf(')');
                        if (closeParen >= 0)
                        {
                            _index += closeBracket + 1;
                            token = new Token(start, _index - start, TokenType.Type);
                            return true;
                        }
                    }
                }
            }
            if (c == '(' && start > 0 && _text[start - 1] == ']')
            {
                // The URL part of the link: (url)
                _index++;
                while (_index < _text.Length && _text[_index] != ')')
                {
                    _index++;
                }
                if (_index < _text.Length) _index++;
                token = new Token(start, _index - start, TokenType.String);
                return true;
            }

            // 4. Bold / Italic **bold** *italic*
            if (c == '*' && _index + 1 < _text.Length && _text[_index + 1] == '*')
            {
                _index += 2;
                while (_index + 1 < _text.Length && !(_text[_index] == '*' && _text[_index + 1] == '*'))
                {
                    _index++;
                }
                if (_index + 1 < _text.Length) _index += 2;
                token = new Token(start, _index - start, TokenType.Keyword);
                return true;
            }

            // Normal text
            _index++;
            while (_index < _text.Length && _text[_index] != '`' && _text[_index] != '[' && _text[_index] != '(' && _text[_index] != '*')
            {
                _index++;
            }
            token = new Token(start, _index - start, TokenType.Text);
            return true;
        }
    }

    public ref struct GenericLexer
    {
        private readonly ReadOnlySpan<char> _text;
        private int _index;
        private readonly RegisteredLanguageConfig _config;
        private LineState _state;

        public GenericLexer(ReadOnlySpan<char> text, RegisteredLanguageConfig config, LineState startState)
        {
            _text = text;
            _index = 0;
            _config = config;
            _state = startState;
        }

        public bool NextToken(out Token token, out LineState nextState)
        {
            if (_index >= _text.Length)
            {
                token = default;
                nextState = _state;
                return false;
            }

            int start = _index;
            char c = _text[_index];

            // 1. Handle stateful block comments (InBlockComment)
            if (_state == LineState.InBlockComment)
            {
                string blockEnd = _config.BlockCommentEnd ?? "*/";
                int endIdx = _text.Slice(_index).IndexOf(blockEnd);
                if (endIdx >= 0)
                {
                    _index += endIdx + blockEnd.Length;
                    _state = LineState.Normal;
                    nextState = LineState.Normal;
                    token = new Token(start, _index - start, TokenType.Comment);
                    return true;
                }
                else
                {
                    _index = _text.Length;
                    nextState = LineState.InBlockComment;
                    token = new Token(start, _index - start, TokenType.Comment);
                    return true;
                }
            }

            // 2. Skip whitespace
            if (char.IsWhiteSpace(c))
            {
                while (_index < _text.Length && char.IsWhiteSpace(_text[_index]))
                {
                    _index++;
                }
                token = new Token(start, _index - start, TokenType.Text);
                nextState = _state;
                return true;
            }

            // 3. Block comment start check
            if (!string.IsNullOrEmpty(_config.BlockCommentStart))
            {
                if (_text.Slice(_index).StartsWith(_config.BlockCommentStart))
                {
                    string blockEnd = _config.BlockCommentEnd ?? "";
                    int endIdx = _text.Slice(_index + _config.BlockCommentStart.Length).IndexOf(blockEnd);
                    if (endIdx >= 0)
                    {
                        _index += _config.BlockCommentStart.Length + endIdx + blockEnd.Length;
                        token = new Token(start, _index - start, TokenType.Comment);
                        _state = LineState.Normal;
                        nextState = LineState.Normal;
                        return true;
                    }
                    else
                    {
                        _index = _text.Length;
                        token = new Token(start, _index - start, TokenType.Comment);
                        _state = LineState.InBlockComment;
                        nextState = LineState.InBlockComment;
                        return true;
                    }
                }
            }

            // 4. Line comment check
            if (!string.IsNullOrEmpty(_config.LineComment))
            {
                if (_text.Slice(_index).StartsWith(_config.LineComment))
                {
                    _index = _text.Length;
                    token = new Token(start, _index - start, TokenType.Comment);
                    nextState = _state;
                    return true;
                }
            }

            // 5. String Literals
            if (c == '"' || c == '\'')
            {
                char quote = c;
                _index++;
                while (_index < _text.Length)
                {
                    if (_text[_index] == '\\' && _index + 1 < _text.Length)
                    {
                        _index += 2;
                        continue;
                    }
                    if (_text[_index] == quote)
                    {
                        _index++;
                        break;
                    }
                    _index++;
                }
                token = new Token(start, _index - start, TokenType.String);
                nextState = _state;
                return true;
            }

            // 6. Number Literals
            if (char.IsDigit(c))
            {
                while (_index < _text.Length && (char.IsLetterOrDigit(_text[_index]) || _text[_index] == '.'))
                {
                    _index++;
                }
                token = new Token(start, _index - start, TokenType.Number);
                nextState = _state;
                return true;
            }

            // 7. Identifiers (keywords, types, text)
            if (char.IsLetter(c) || c == '_')
            {
                while (_index < _text.Length && (char.IsLetterOrDigit(_text[_index]) || _text[_index] == '_'))
                {
                    _index++;
                }

                ReadOnlySpan<char> word = _text.Slice(start, _index - start);
                string wordStr = word.ToString();

                TokenType type = TokenType.Text;
                if (_config.Keywords.Contains(wordStr))
                {
                    type = TokenType.Keyword;
                }
                else if (_config.Types.Contains(wordStr))
                {
                    type = TokenType.Type;
                }

                token = new Token(start, _index - start, type);
                nextState = _state;
                return true;
            }

            // 8. Operators / punctuation fallback
            _index++;
            token = new Token(start, 1, TokenType.Text);
            nextState = _state;
            return true;
        }
    }

    public ref struct DocumentLexer
    {
        private enum LexerType : byte
        {
            CSharp,
            Json,
            Html,
            Css,
            JavaScript,
            Markdown,
            Generic,
            Plain
        }

        private readonly LexerType _type;
        private CSharpLexer _csharpLexer;
        private JsonLexer _jsonLexer;
        private HtmlLexer _htmlLexer;
        private CssLexer _cssLexer;
        private JavaScriptLexer _jsLexer;
        private MarkdownLexer _mdLexer;
        private GenericLexer _genericLexer;
        private PlainLexer _plainLexer;

        public DocumentLexer(ReadOnlySpan<char> text, string extension, LineState startState)
        {
            var coreType = GetLexerType(extension);
            if (coreType != LexerType.Plain)
            {
                _type = coreType;
                switch (_type)
                {
                    case LexerType.CSharp:
                        _csharpLexer = new CSharpLexer(text, startState);
                        break;
                    case LexerType.Json:
                        _jsonLexer = new JsonLexer(text);
                        break;
                    case LexerType.Html:
                        _htmlLexer = new HtmlLexer(text, startState);
                        break;
                    case LexerType.Css:
                        _cssLexer = new CssLexer(text);
                        break;
                    case LexerType.JavaScript:
                        _jsLexer = new JavaScriptLexer(text, startState);
                        break;
                    case LexerType.Markdown:
                        _mdLexer = new MarkdownLexer(text);
                        break;
                }
            }
            else
            {
                var registered = LanguageConfigurationRegistry.Get(extension);
                if (registered != null && (registered.Keywords.Count > 0 || registered.Types.Count > 0 || registered.LineComment != null || registered.BlockCommentStart != null))
                {
                    _type = LexerType.Generic;
                    _genericLexer = new GenericLexer(text, registered, startState);
                }
                else
                {
                    _type = LexerType.Plain;
                    _plainLexer = new PlainLexer(text);
                }
            }
        }

        private static LexerType GetLexerType(string extension)
        {
            if (string.IsNullOrEmpty(extension)) return LexerType.Plain;
            extension = extension.ToLowerInvariant();
            return extension switch
            {
                ".cs" or ".csx" => LexerType.CSharp,
                ".json" => LexerType.Json,
                ".html" or ".htm" or ".xml" or ".csproj" or ".slnx" or ".props" or ".targets" or ".config" or ".settings" or ".resx" or ".pubxml" or ".user" or ".nuspec" => LexerType.Html,
                ".css" => LexerType.Css,
                ".js" => LexerType.JavaScript,
                ".md" => LexerType.Markdown,
                _ => LexerType.Plain
            };
        }

        public bool NextToken(out Token token, out LineState nextState)
        {
            switch (_type)
            {
                case LexerType.CSharp:
                    return _csharpLexer.NextToken(out token, out nextState);
                case LexerType.Json:
                    nextState = LineState.Normal;
                    return _jsonLexer.NextToken(out token);
                case LexerType.Html:
                    return _htmlLexer.NextToken(out token, out nextState);
                case LexerType.Css:
                    nextState = LineState.Normal;
                    return _cssLexer.NextToken(out token);
                case LexerType.JavaScript:
                    return _jsLexer.NextToken(out token, out nextState);
                case LexerType.Markdown:
                    nextState = LineState.Normal;
                    return _mdLexer.NextToken(out token);
                case LexerType.Generic:
                    return _genericLexer.NextToken(out token, out nextState);
                default:
                    nextState = LineState.Normal;
                    return _plainLexer.NextToken(out token);
            }
        }

        public static LineState ComputeEndState(ReadOnlySpan<char> text, string extension, LineState startState)
        {
            var lexer = new DocumentLexer(text, extension, startState);
            while (lexer.NextToken(out _, out var nextState))
            {
                startState = nextState;
            }
            return startState;
        }
    }
}
