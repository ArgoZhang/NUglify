// jsscanner.cs
//
// Copyright 2010 Microsoft Corporation
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using NUglify.Helpers;
using NUglify.JavaScript.Syntax;

namespace NUglify.JavaScript
{
    public sealed class JSScanner
    {
        // keyword table
        static readonly JSKeyword[] keywords = JSKeyword.InitKeywords();
        static readonly OperatorPrecedence[] operatorsPrec = InitOperatorsPrec();
        static readonly bool[] validIdentifierPartMap = InitializeValidIdentifierPartMap();

        readonly StringBuilder identifier;

        string sourceCode;
        int endPos;

        // a list of strings that we can add new ones to or clear depending on comments we may find in the source
        internal ICollection<string> DebugLookupCollection { get; set; }
        Dictionary<string, string> defines;
        int currentPosition;
        int lastPosOnBuilder;
        int ifDirectiveLevel;
        int conditionalCompilationIfLevel;
        bool conditionalCompilationOn;
        bool inConditionalComment;
        bool inSingleLineComment;
        bool inMultipleLineComment;
        bool mightBeKeyword;
        JSScanner peekClone;



        /// <summary>
        /// Gets or sets whether to use NUglify preprocessor defines or ignore them
        /// </summary>
        public bool UsePreprocessorDefines { get; set; }

        /// <summary>
        /// Gets or sets whether to completely ignore IE conditional-compilation comments
        /// </summary>
        public bool IgnoreConditionalCompilation { get; set; }

        /// <summary>
        /// Gets or sets whether to allow ASP.NET &lt;% ... %&gt; syntax within the script
        /// </summary>
        public bool AllowEmbeddedAspNetBlocks { get; set; }

        /// <summary>
        /// Gets or sets whether to strip debug comment blocks entirely
        /// </summary>
        public bool StripDebugCommentBlocks { get; set; }

        /// <summary>
        /// Gets or sets whether to suppress all scanning errors
        /// </summary>
        public bool SuppressErrors { get; set; }

        /// <summary>
        /// Gets the current line of the input file
        /// </summary>
        public int CurrentLine { get; private set; }

        /// <summary>
        /// Gets whether we have passed the end of the input source
        /// </summary>
        public bool IsEndOfFile => currentPosition >= endPos;

        /// <summary>
        /// Gets the position within the source of the start of the current line
        /// </summary>
        public int StartLinePosition { get; private set; }

        /// <summary>
        /// Gets whether the scanned literal has potential cross-browser issues
        /// </summary>
        public bool LiteralHasIssues { get; private set; }

        /// <summary>
        /// Gets the current decoded string literal value
        /// </summary>
        public string StringLiteralValue { get; private set; }

        /// <summary>
        /// Gets the current decoded identifier string
        /// </summary>
        public string Identifier =>
            identifier.Length > 0
                ? identifier.ToString()
                : CurrentToken.Code;

        /// <summary>
        /// Gets the current token reference
        /// </summary>
        public SourceContext CurrentToken { get; private set; }

        bool IsAtEndOfLine => IsEndLineOrEOF(GetChar(currentPosition), 0);


        #region public events

        public event EventHandler<GlobalDefineEventArgs> GlobalDefine;

        public event EventHandler<NewModuleEventArgs> NewModule;

        #endregion

        #region constructors

        public JSScanner(DocumentContext sourceContext)
        {
            if (sourceContext == null)
                throw new ArgumentNullException("sourceContext");

            // create a new empty context. By default the constructor will make the context
            // represent the entire document, but we want to start it off at just the beginning of it.
            CurrentToken = new SourceContext(sourceContext)
            {
                EndPosition = 0
            };
            CurrentLine = 1;

            // just hold on to these values so we don't have to keep dereferencing them
            // from the current token
            sourceCode = sourceContext.Source;
            endPos = sourceContext.Source.Length;

            // by default we want to use preprocessor defines
            // and strip debug comment blocks
            UsePreprocessorDefines = true;
            StripDebugCommentBlocks = true;

            // create a string builder that we'll keep reusing as we
            // scan identifiers. We'll build the unescaped name into it
            identifier = new StringBuilder(128);
        }

        // used only in the Clone method below
        JSScanner(IDictionary<string, string> defines)
        {
            // copy the collection, but don't share it. We don't want
            // defines we find here to populate the collection of the original.
            SetPreprocessorDefines(defines);

            // stuff we don't want to copy
            StringLiteralValue = null;
            identifier = new StringBuilder(128);

            // create a new set so anything we find doesn't affect the original.
            DebugLookupCollection = new HashSet<string>();
        }

        /// <summary>
        /// Clone for peek, reuse the same instance
        /// </summary>
        /// <returns></returns>
        internal JSScanner PeekClone()
        {
            if (peekClone == null)
                peekClone = new JSScanner(this.defines);

            peekClone.AllowEmbeddedAspNetBlocks = this.AllowEmbeddedAspNetBlocks;
            peekClone.IgnoreConditionalCompilation = this.IgnoreConditionalCompilation;
            peekClone.conditionalCompilationIfLevel = this.conditionalCompilationIfLevel;
            peekClone.conditionalCompilationOn = this.conditionalCompilationOn;
            peekClone.CurrentLine = this.CurrentLine;
            peekClone.currentPosition = this.currentPosition;
            peekClone.CurrentToken = this.CurrentToken.Clone();
            peekClone.endPos = this.endPos;
            peekClone.ifDirectiveLevel = this.ifDirectiveLevel;
            peekClone.inConditionalComment = this.inConditionalComment;
            peekClone.inMultipleLineComment = this.inMultipleLineComment;
            peekClone.inSingleLineComment = this.inSingleLineComment;
            peekClone.lastPosOnBuilder = this.lastPosOnBuilder;
            peekClone.StartLinePosition = this.StartLinePosition;
            peekClone.sourceCode = this.sourceCode;
            peekClone.UsePreprocessorDefines = this.UsePreprocessorDefines;
            peekClone.StripDebugCommentBlocks = this.StripDebugCommentBlocks;

            return peekClone;
        }

        public JSScanner Clone()
        {
            return new JSScanner(this.defines)
            {
                AllowEmbeddedAspNetBlocks = this.AllowEmbeddedAspNetBlocks,
                IgnoreConditionalCompilation = this.IgnoreConditionalCompilation,
                conditionalCompilationIfLevel = this.conditionalCompilationIfLevel,
                conditionalCompilationOn = this.conditionalCompilationOn,
                CurrentLine = this.CurrentLine,
                currentPosition = this.currentPosition,
                CurrentToken = this.CurrentToken.Clone(),
                endPos = this.endPos,
                ifDirectiveLevel = this.ifDirectiveLevel,
                inConditionalComment = this.inConditionalComment,
                inMultipleLineComment = this.inMultipleLineComment,
                inSingleLineComment = this.inSingleLineComment,
                lastPosOnBuilder = this.lastPosOnBuilder,
                StartLinePosition = this.StartLinePosition,
                sourceCode = this.sourceCode,
                UsePreprocessorDefines = this.UsePreprocessorDefines,
                StripDebugCommentBlocks = this.StripDebugCommentBlocks,
            };
        }

        #endregion

        #region public methods

        /// <summary>
        /// Set the list of preprocessor defined names and values
        /// </summary>
        /// <param name="defines">dictionary of name/value pairs</param>
        public void SetPreprocessorDefines(IDictionary<string, string> defines)
        {
            // this is a destructive set, blowing away any previous list
            if (defines != null && defines.Count > 0)
            {
                // create a new dictionary, case-INsensitive
                this.defines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                // add an entry for each unique, valid name passed to us.
                foreach (var nameValuePair in defines)
                {
                    if (JSScanner.IsValidIdentifier(nameValuePair.Key) && !this.defines.ContainsKey(nameValuePair.Key))
                        this.defines.Add(nameValuePair.Key, nameValuePair.Value);
                }
            }
            else
            {
                // we have no defined names
                this.defines = null;
            }
        }

        /// <summary>
        /// main method for the scanner; scans the next token from the input stream.
        /// </summary>
        /// <returns>next token from the input</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA1502:AvoidExcessiveComplexity"),
         System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA1505:AvoidUnmaintainableCode", Justification = "big case statement")]
        public SourceContext ScanNextToken()
        {
            JSToken token;
            char nextChar;

            CurrentToken.StartPosition = currentPosition;
            CurrentToken.StartLineNumber = CurrentLine;
            CurrentToken.StartLinePosition = StartLinePosition;

            identifier.Length = 0;
            mightBeKeyword = false;

            // our case switch should be pretty efficient -- it's 9-13 and 32-126. Thsose are the most common characters 
            // we will find in the code for the start of tokens.
            char ch = GetChar(currentPosition);
            switch (ch)
            {
                case '\n':
                case '\r':
                    token = ScanLineTerminator(ch);
                    break;

                case '\t':
                case '\v':
                case '\f':
                case ' ':
                    // we are asking for raw tokens, and this is the start of a stretch of whitespace.
                    // advance to the end of the whitespace, and return that as the token
                    token = JSToken.WhiteSpace;
                    while (JSScanner.IsBlankSpace(GetChar(++currentPosition)))
                    {
                        // increment handled by condition
                    }

                    break;

                case '!':
                    token = JSToken.LogicalNot;
                    if ('=' == GetChar(++currentPosition))
                    {
                        token = JSToken.NotEqual;
                        if ('=' == GetChar(++currentPosition))
                        {
                            ++currentPosition;
                            token = JSToken.StrictNotEqual;
                        }
                    }

                    break;

                case '"':
                case '\'':
                    token = JSToken.StringLiteral;
                    ScanString(ch);
                    break;

                case '$':
                case '_':
                case '#':
                    token = ScanIdentifier(true);
                    break;

                case '%':
                    token = JSToken.Modulo;
                    if ('=' == GetChar(++currentPosition))
                    {
                        ++currentPosition;
                        token = JSToken.ModuloAssign;
                    }

                    break;

                case '&':
                    token = JSToken.BitwiseAnd;
                    nextChar = GetChar(++currentPosition);

                    if ('=' == nextChar)
                    {
                        ++currentPosition;
                        token = JSToken.BitwiseAndAssign;
                    }
                    else if ('&' == nextChar)
                    {
                        token = JSToken.LogicalAnd;
                        nextChar = GetChar(++currentPosition);

                        if ('=' == nextChar)
                        {
                            token = JSToken.LogicalAndAssign;
                            ++currentPosition;
                        }
                    }

                    break;

                case '(':
                    token = JSToken.LeftParenthesis;
                    ++currentPosition;
                    break;

                case ')':
                    token = JSToken.RightParenthesis;
                    ++currentPosition;
                    break;

                case '*':
                    token = JSToken.Multiply;
                    nextChar = GetChar(++currentPosition);

                    if ('=' == nextChar)
                    {
                        ++currentPosition;
                        token = JSToken.MultiplyAssign;
                    }
                    else if ('*' == nextChar)
                    {
                        token = JSToken.Exponent;
                        nextChar = GetChar(++currentPosition);

                        if ('=' == nextChar)
                        {
                            token = JSToken.ExponentAssign;
                            ++currentPosition;
                        }
                    }

                    break;

                case '+':
                    token = JSToken.Plus;
                    ch = GetChar(++currentPosition);
                    if ('+' == ch)
                    {
                        ++currentPosition;
                        token = JSToken.Increment;
                    }
                    else if ('=' == ch)
                    {
                        ++currentPosition;
                        token = JSToken.PlusAssign;
                    }

                    break;

                case ',':
                    token = JSToken.Comma;
                    ++currentPosition;
                    break;

                case '-':
                    token = JSToken.Minus;
                    ch = GetChar(++currentPosition);
                    if ('-' == ch)
                    {
                        ++currentPosition;
                        token = JSToken.Decrement;
                    }
                    else if ('=' == ch)
                    {
                        ++currentPosition;
                        token = JSToken.MinusAssign;
                    }

                    break;

                case '.':
                    token = JSToken.AccessField;
                    ch = GetChar(++currentPosition);
                    if (ch == '.' && GetChar(++currentPosition) == '.')
                    {
                        token = JSToken.RestSpread;
                        ++currentPosition;
                    }
                    else if (IsDigit(ch))
                    {
                        token = ScanNumber('.');
                    }

                    break;

                case '/':
                    token = JSToken.Divide;
                    ch = GetChar(++currentPosition);
                    switch (ch)
                    {
                        case '/':
                            token = JSToken.SingleLineComment;
                            inSingleLineComment = true;
                            ch = GetChar(++currentPosition);

                            // see if there is a THIRD slash character
                            if (ch == '/')
                            {
                                // advance past the slash and see if we have one of our special preprocessing directives
                                if (GetChar(++currentPosition) == '#')
                                {
                                    // scan preprocessing directives
                                    token = JSToken.PreprocessorDirective;

                                    if (!ScanPreprocessingDirective())
                                    {
                                        // if it returns false, we don't want to skip the rest of
                                        // the comment line; just exit
                                        break;
                                    }
                                }
                            }
                            else if (ch == '@' && !IgnoreConditionalCompilation)
                            {
                                // we got //@
                                // if we have not turned on conditional-compilation yet, then check to see if that's
                                // what we're trying to do now.
                                // we are currently on the @ -- start peeking from there
                                if (conditionalCompilationOn
                                    || CheckSubstring(currentPosition + 1, "cc_on"))
                                {
                                    // if the NEXT character is not an identifier character, then we need to skip
                                    // the @ character -- otherwise leave it there
                                    if (!IsValidIdentifierStart(sourceCode, currentPosition + 1))
                                    {
                                        ++currentPosition;
                                    }

                                    // we are now in a conditional comment
                                    inConditionalComment = true;
                                    token = JSToken.ConditionalCommentStart;
                                    break;
                                }
                            }

                            SkipSingleLineComment();

                            // if we're still in a multiple-line comment, then we must've been in
                            // a multi-line CONDITIONAL comment, in which case this normal one-line comment
                            // won't turn off conditional comments just because we hit the end of line.
                            if (!inMultipleLineComment && inConditionalComment)
                            {
                                inConditionalComment = false;
                                token = JSToken.ConditionalCommentEnd;
                            }

                            break;

                        case '*':
                            inMultipleLineComment = true;
                            token = JSToken.MultipleLineComment;
                            ch = GetChar(++currentPosition);
                            if (ch == '@' && !IgnoreConditionalCompilation)
                            {
                                // we have /*@
                                // if we have not turned on conditional-compilation yet, then let's peek to see if the next
                                // few characters are cc_on -- if so, turn it on.
                                if (!conditionalCompilationOn)
                                {
                                    // we are currently on the @ -- start peeking from there
                                    if (!CheckSubstring(currentPosition + 1, "cc_on"))
                                    {
                                        // we aren't turning on conditional comments. We need to ignore this comment
                                        // as just another multi-line comment
                                        SkipMultilineComment();
                                        token = JSToken.MultipleLineComment;
                                        break;
                                    }
                                }

                                // if the NEXT character is not an identifier character, then we need to skip
                                // the @ character -- otherwise leave it there
                                if (!IsValidIdentifierStart(sourceCode, currentPosition + 1))
                                {
                                    ++currentPosition;
                                }

                                // we are now in a conditional comment
                                inConditionalComment = true;
                                token = JSToken.ConditionalCommentStart;
                                break;
                            }
                            else if (ch == '/')
                            {
                                // We have /*/
                                // advance past the slash and see if we have one of our special preprocessing directives
                                if (GetChar(++currentPosition) == '#')
                                {
                                    // scan preprocessing directives. When it exits we will still be within
                                    // the multiline comment, since none of the directives should eat the closing */.
                                    // therefore no matter the reason we exist the scan, we always want to skip
                                    // the rest of the multiline comment.
                                    token = JSToken.PreprocessorDirective;
                                    if (!ScanPreprocessingDirective())
                                    {
                                        // if it returns false, we fully-procesesed the comment and
                                        // and just want to exit now.
                                        break;
                                    }
                                }
                            }

                            // skip the rest of the comment
                            SkipMultilineComment();
                            break;

                        case '=':
                            currentPosition++;
                            token = JSToken.DivideAssign;
                            break;
                    }
                    break;

                case '0':
                case '1':
                case '2':
                case '3':
                case '4':
                case '5':
                case '6':
                case '7':
                case '8':
                case '9':
                    ++currentPosition;
                    token = ScanNumber(ch);
                    break;

                case ':':
                    token = JSToken.Colon;
                    ++currentPosition;
                    break;

                case ';':
                    token = JSToken.Semicolon;
                    ++currentPosition;
                    break;

                case '<':
                    if (AllowEmbeddedAspNetBlocks &&
                        '%' == GetChar(++currentPosition))
                    {
                        token = ScanAspNetBlock();
                    }
                    else
                    {
                        token = JSToken.LessThan;
                        if ('<' == GetChar(++currentPosition))
                        {
                            ++currentPosition;
                            token = JSToken.LeftShift;
                        }

                        if ('=' == GetChar(currentPosition))
                        {
                            ++currentPosition;
                            token = token == JSToken.LessThan
                                ? JSToken.LessThanEqual
                                : JSToken.LeftShiftAssign;
                        }
                    }
                    break;

                case '=':
                    token = JSToken.Assign;
                    if ('=' == GetChar(++currentPosition))
                    {
                        token = JSToken.Equal;
                        if ('=' == GetChar(++currentPosition))
                        {
                            ++currentPosition;
                            token = JSToken.StrictEqual;
                        }
                    }
                    else if (GetChar(currentPosition) == '>')
                    {
                        ++currentPosition;
                        token = JSToken.ArrowFunction;
                    }

                    break;

                case '>':
                    token = JSToken.GreaterThan;
                    if ('>' == GetChar(++currentPosition))
                    {
                        token = JSToken.RightShift;
                        if ('>' == GetChar(++currentPosition))
                        {
                            ++currentPosition;
                            token = JSToken.UnsignedRightShift;
                        }
                    }

                    if ('=' == GetChar(currentPosition))
                    {
                        ++currentPosition;
                        token = token == JSToken.GreaterThan ? JSToken.GreaterThanEqual
                            : token == JSToken.RightShift ? JSToken.RightShiftAssign
                            : token == JSToken.UnsignedRightShift ? JSToken.UnsignedRightShiftAssign
                            : JSToken.Error;
                    }
                    break;

                case '?':
                    token = JSToken.ConditionalIf;
                    ++currentPosition;

                    ch = GetChar(currentPosition);
                    if ('?' == ch)
                    {
                        token = JSToken.NullishCoalesce;

                        if (GetChar(++currentPosition) == '=')
                        {
                            token = JSToken.LogicalNullishAssign;
                            ++currentPosition;
                        }
                    }
                    else if ('.' == GetChar(currentPosition))
                    {
                        // ensure x?.1:2 is treated as a conditional expression not optional chaining
                        var chNext = GetChar(currentPosition + 1);
                        if (chNext < '0' || chNext > '9')
                        {
                            token = JSToken.OptionalChaining;
                            ++currentPosition;
                        }
                    }

                    break;

                case '@':
                    if (IgnoreConditionalCompilation)
                    {
                        // if the switch to ignore conditional compilation is on, then we don't know
                        // anything about conditional-compilation statements, and the @-sign character
                        // is illegal at this spot.
                        ++currentPosition;
                        token = IllegalCharacter();
                        break;
                    }

                    // see if the @-sign is immediately followed by an identifier. If it is,
                    // we'll see which one so we can tell if it's a conditional-compilation statement
                    // need to make sure the context INCLUDES the @ sign
                    int startPosition = ++currentPosition;
                    ScanIdentifier(false);
                    switch (currentPosition - startPosition)
                    {
                        case 0:
                            // look for '@*/'.
                            if ('*' == GetChar(currentPosition) && '/' == GetChar(currentPosition + 1))
                            {
                                currentPosition += 2;
                                inMultipleLineComment = false;
                                inConditionalComment = false;
                                token = JSToken.ConditionalCommentEnd;
                                break;
                            }

                            // otherwise we just have a @ sitting by itself!
                            // throw an error and loop back to the next token.
                            token = IllegalCharacter();
                            break;

                        case 2:
                            if (CheckSubstring(startPosition, "if"))
                            {
                                token = JSToken.ConditionalCompilationIf;

                                // increment the if-level
                                ++conditionalCompilationIfLevel;

                                // if we're not in a conditional comment and we haven't explicitly
                                // turned on conditional compilation when we encounter
                                // a @if statement, then we can implicitly turn it on.
                                if (!inConditionalComment && !conditionalCompilationOn)
                                {
                                    conditionalCompilationOn = true;
                                }

                                break;
                            }

                            // the string isn't a known preprocessor command, so 
                            // fall into the default processing to handle it as a variable name
                            goto default;

                        case 3:
                            if (CheckSubstring(startPosition, "set"))
                            {
                                token = JSToken.ConditionalCompilationSet;

                                // if we're not in a conditional comment and we haven't explicitly
                                // turned on conditional compilation when we encounter
                                // a @set statement, then we can implicitly turn it on.
                                if (!inConditionalComment && !conditionalCompilationOn)
                                {
                                    conditionalCompilationOn = true;
                                }

                                break;
                            }

                            if (CheckSubstring(startPosition, "end"))
                            {
                                token = JSToken.ConditionalCompilationEnd;
                                if (conditionalCompilationIfLevel > 0)
                                {
                                    // down one more @if level
                                    conditionalCompilationIfLevel--;
                                }
                                else
                                {
                                    // not corresponding @if -- invalid @end statement
                                    HandleError(JSError.CCInvalidEnd);
                                }

                                break;
                            }

                            // the string isn't a known preprocessor command, so 
                            // fall into the default processing to handle it as a variable name
                            goto default;

                        case 4:
                            if (CheckSubstring(startPosition, "else"))
                            {
                                token = JSToken.ConditionalCompilationElse;

                                // if we don't have a corresponding @if statement, then throw and error
                                // (but keep processing)
                                if (conditionalCompilationIfLevel <= 0)
                                {
                                    HandleError(JSError.CCInvalidElse);
                                }

                                break;
                            }

                            if (CheckSubstring(startPosition, "elif"))
                            {
                                token = JSToken.ConditionalCompilationElseIf;

                                // if we don't have a corresponding @if statement, then throw and error
                                // (but keep processing)
                                if (conditionalCompilationIfLevel <= 0)
                                {
                                    HandleError(JSError.CCInvalidElseIf);
                                }

                                break;
                            }

                            // the string isn't a known preprocessor command, so 
                            // fall into the default processing to handle it as a variable name
                            goto default;

                        case 5:
                            if (CheckSubstring(startPosition, "cc_on"))
                            {
                                // turn it on and return the @cc_on token
                                conditionalCompilationOn = true;
                                token = JSToken.ConditionalCompilationOn;
                                break;
                            }

                            // the string isn't a known preprocessor command, so 
                            // fall into the default processing to handle it as a variable name
                            goto default;

                        default:
                            // we have @[id], where [id] is a valid identifier.
                            // if we haven't explicitly turned on conditional compilation,
                            // we'll keep processing, but we need to fire an error to indicate
                            // that the code should turn it on first.
                            if (!conditionalCompilationOn)
                            {
                                HandleError(JSError.CCOff);
                            }

                            token = JSToken.ConditionalCompilationVariable;
                            break;
                    }

                    break;

                case 'A':
                case 'B':
                case 'C':
                case 'D':
                case 'E':
                case 'F':
                case 'G':
                case 'H':
                case 'I':
                case 'J':
                case 'K':
                case 'L':
                case 'M':
                case 'N':
                case 'O':
                case 'P':
                case 'Q':
                case 'R':
                case 'S':
                case 'T':
                case 'U':
                case 'V':
                case 'W':
                case 'X':
                case 'Y':
                case 'Z':
                    token = ScanIdentifier(true);
                    break;

                case '[':
                    token = JSToken.LeftBracket;
                    ++currentPosition;
                    break;

                case '\\':
                    // try decoding the unicode escape sequence and checking for a valid identifier start
                    token = ScanIdentifier(true);
                    if (token != JSToken.Identifier)
                    {
                        if (GetChar(currentPosition + 1) == 'u')
                        {
                            // it was a unicode escape -- move past the whole "character" and mark it as illegal
                            var beforePeek = currentPosition;
                            PeekUnicodeEscape(sourceCode, ref currentPosition);

                            var count = currentPosition - beforePeek;
                            if (count > 1)
                            {
                                // the whole escape sequence is an invalid character
                                HandleError(JSError.IllegalChar);
                            }
                            else
                            {
                                // just the slash. Must not be a valid unicode escape.
                                // treat like a badly-escaped identifier, like: \umber
                                token = ScanIdentifier(true);
                                HandleError(JSError.BadHexEscapeSequence);
                            }
                        }
                        else if (IsValidIdentifierStart(sourceCode, currentPosition + 1))
                        {
                            // if the NEXT character after the backslash is a valid identifier start
                            // then we're just going to assume we had something like \while,
                            // in which case we scan the identifier AFTER the slash
                            ++currentPosition;
                            token = ScanIdentifier(true);
                        }
                        else
                        {
                            // the one character is illegal
                            ++currentPosition;
                            HandleError(JSError.IllegalChar);
                        }
                    }
                    break;

                case ']':
                    token = JSToken.RightBracket;
                    ++currentPosition;
                    break;

                case '^':
                    token = JSToken.BitwiseXor;
                    if ('=' == GetChar(++currentPosition))
                    {
                        ++currentPosition;
                        token = JSToken.BitwiseXorAssign;
                    }

                    break;

                // case '#':
                //     ++currentPosition;
                //     token = IllegalCharacter();
                //     break;

                case '`':
                    // start a template literal
                    token = ScanTemplateLiteral(ch);
                    break;

                case 'a':
                case 'b':
                case 'c':
                case 'd':
                case 'e':
                case 'f':
                case 'g':
                case 'h':
                case 'i':
                case 'j':
                case 'k':
                case 'l':
                case 'm':
                case 'n':
                case 'o':
                case 'p':
                case 'q':
                case 'r':
                case 's':
                case 't':
                case 'u':
                case 'v':
                case 'w':
                case 'x':
                case 'y':
                case 'z':
                    mightBeKeyword = true;
                    token = ScanKeyword(keywords[ch - 'a']);
                    break;

                case '{':
                    token = JSToken.LeftCurly;
                    ++currentPosition;
                    break;

                case '|':
                    token = JSToken.BitwiseOr;
                    nextChar = GetChar(++currentPosition);

                    if ('=' == nextChar)
                    {
                        ++currentPosition;
                        token = JSToken.BitwiseOrAssign;
                    }
                    else if ('|' == nextChar)
                    {
                        token = JSToken.LogicalOr;
                        nextChar = GetChar(++currentPosition);

                        if ('=' == nextChar)
                        {
                            token = JSToken.LogicalOrAssign;
                            ++currentPosition;
                        }
                    }
                    break;

                case '}':
                    // just a regular close curly-brace.
                    token = JSToken.RightCurly;
                    ++currentPosition;
                    break;

                case '~':
                    token = JSToken.BitwiseNot;
                    ++currentPosition;
                    break;

                default:
                    if (ch == '\0')
                    {
                        if (IsEndOfFile)
                        {
                            token = JSToken.EndOfFile;
                            if (conditionalCompilationIfLevel > 0)
                            {
                                CurrentToken.EndLineNumber = CurrentLine;
                                CurrentToken.EndLinePosition = StartLinePosition;
                                CurrentToken.EndPosition = currentPosition;
                                HandleError(JSError.NoCCEnd);
                            }
                        }
                        else
                        {
                            ++currentPosition;
                            token = IllegalCharacter();
                        }
                    }
                    else if (ch == '\u2028' || ch == '\u2029')
                    {
                        // more line terminator
                        token = ScanLineTerminator(ch);
                    }
                    else if (0xd800 <= ch && ch <= 0xdbff)
                    {
                        // high-surrogate
                        var lowSurrogate = GetChar(currentPosition + 1);
                        if (0xdc00 <= lowSurrogate && lowSurrogate <= 0xdfff)
                        {
                            // use the surrogate pair
                            token = ScanIdentifier(true);
                            if (token != JSToken.Identifier)
                            {
                                // this surrogate pair isn't the start of an identifier,
                                // so together they are illegal here.
                                currentPosition += 2;
                                token = IllegalCharacter();
                            }
                        }
                        else
                        {
                            // high-surrogate NOT followed by a low surrogate
                            ++currentPosition;
                            token = IllegalCharacter();
                        }
                    }
                    else if (IsValidIdentifierStart(sourceCode, currentPosition))
                    {
                        token = ScanIdentifier(true);
                    }
                    else if (IsBlankSpace(ch))
                    {
                        // we are asking for raw tokens, and this is the start of a stretch of whitespace.
                        // advance to the end of the whitespace, and return that as the token
                        token = JSToken.WhiteSpace;
                        while (JSScanner.IsBlankSpace(GetChar(++currentPosition)))
                        {
                            // increment handled in condition
                        }
                    }
                    else
                    {
                        ++currentPosition;
                        token = IllegalCharacter();
                    }

                    break;
            }

            // fix up the end of the token
            CurrentToken.EndLineNumber = CurrentLine;
            CurrentToken.EndLinePosition = StartLinePosition;
            CurrentToken.EndPosition = currentPosition;

            // this is now the current token
            CurrentToken.Token = token;
            return CurrentToken;
        }

        public SourceContext UpdateToken(UpdateHint updateHint)
        {
            if (updateHint == UpdateHint.RegularExpression
                && (CurrentToken.IsEither(JSToken.Divide, JSToken.DivideAssign)))
            {
                CurrentToken.Token = ScanRegExp();
            }
            else if (updateHint == UpdateHint.TemplateLiteral && CurrentToken.Is(JSToken.RightCurly))
            {
                // update the current token with the new ending; don't change the starting information
                CurrentToken.Token = ScanTemplateLiteral('}');
            }
            else if (updateHint == UpdateHint.ReplacementToken && CurrentToken.Is(JSToken.Modulo))
            {
                CurrentToken.Token = ScanReplacementToken();
            }

            // update the end point of the current token, not the start since we're updating,
            // not finding a whole new token.
            CurrentToken.EndLineNumber = CurrentLine;
            CurrentToken.EndLinePosition = StartLinePosition;
            CurrentToken.EndPosition = currentPosition;
            return CurrentToken;
        }

        #endregion

        #region public static methods

        /// <summary>
        /// Returns true if the character is between '0' and '9' inclusive.
        /// Can't use char.IsDigit because that returns true for decimal digits in other language scripts as well.
        /// </summary>
        /// <param name="character">character to test</param>
        /// <returns>true if in ['0' '1' '2' '3' '4' '5' '6' '7' '8' '9']; false otherwise</returns>
        public static bool IsDigit(char character)
        {
            return '0' <= character && character <= '9';
        }

        public static bool IsKeyword(string name, bool strictMode)
        {
            bool isKeyword = false;

            // get the index into the keywords array by taking the first letter of the string
            // and subtracting the character 'a' from it. Use a negative number if the string
            // is null or empty
            if (name != null)
            {
                var index = name[0] - 'a';

                // only proceed if the index is within the array length
                if (0 <= index && index < keywords.Length)
                {
                    // get the head of the list for this index (if any)
                    var keyword = keywords[name[0] - 'a'];
                    if (keyword != null)
                    {
                        // switch off the token
                        switch (keyword.GetKeyword(name, 0, name.Length))
                        {
                            case JSToken.Get:
                            case JSToken.Set:
                            case JSToken.Identifier:
                                // never considered keywords
                                isKeyword = false;
                                break;

                            case JSToken.Implements:
                            case JSToken.Interface:
                            case JSToken.Let:
                            case JSToken.Package:
                            case JSToken.Private:
                            case JSToken.Protected:
                            case JSToken.Public:
                            case JSToken.Static:
                            case JSToken.Yield:
                            case JSToken.Await:
                                // in strict mode, these ARE keywords, otherwise they are okay
                                // to be identifiers
                                isKeyword = strictMode;
                                break;

                            case JSToken.Native:
                            default:
                                // no other tokens can be identifiers.
                                // apparently never allowed for Chrome, so we want to treat it
                                // differently, too
                                isKeyword = true;
                                break;
                        }
                    }
                }
            }

            return isKeyword;
        }

        /// <summary>
        /// Determines whether an unescaped string is a valid identifier.
        /// escape sequences and surrogate pairs are accounted for.
        /// </summary>
        /// <param name="name">potential identifier string</param>
        /// <returns>true if a valid JavaScript identifier; otherwise false</returns>
        public static bool IsValidIdentifier(string name)
        {
            bool isValid = false;
            if (name != null)
            {
                var index = 0;
                if (IsValidIdentifierStart(name, ref index))
                {
                    // loop through all the rest
                    isValid = true;
                    while (index < name.Length)
                    {
                        if (!IsValidIdentifierPart(name, ref index))
                        {
                            // fail!
                            isValid = false;
                            break;
                        }
                    }
                }
            }

            return isValid;
        }

        /// <summary>
        /// Determines if the character(s) at the given index is a valid identifier start
        /// </summary>
        /// <param name="text">potential identifier string</param>
        /// <param name="index">index of the starting character</param>
        /// <returns>true if the character at the given position is a valid identifier start</returns>
        static bool IsValidIdentifierStart(string text, int index)
        {
            return IsValidIdentifierStart(text, ref index);
        }

        /// <summary>
        /// Determines if the character(s) at the given index is a valid identifier start,
        /// and adjust the index to point to the following character
        /// </summary>
        /// <param name="name"></param>
        /// <param name="startIndex">potential identifier string</param>
        /// <returns>true if the character at the given position is a valid identifier start</returns>
        static bool IsValidIdentifierStart(string name, ref int startIndex)
        {
            var isValid = false;

            // pull the first character from the string, which may be an escape character
            if (name != null && startIndex < name.Length)
            {
                var index = startIndex;
                char ch = name[index];
                if (ch == '\\')
                {
                    // unescape the escape sequence(s)
                    // might use two sequences if they are an escaped surrogate pair
                    name = PeekUnicodeEscape(name, ref index);
                    if (name != null && IsValidIdentifierStart(name, 0, name.Length))
                    {
                        startIndex = index;
                        isValid = true;
                    }
                }
                else
                {
                    if (0xd800 <= ch && ch <= 0xdbff)
                    {
                        // high-order surrogate
                        ch = name[++index];
                        if (0xdc00 <= ch && ch <= 0xdfff)
                        {
                            // surrogate pair
                            ++index;
                        }
                    }
                    else
                    {
                        // just the one character
                        ++index;
                    }

                    // no escape sequence, but might be a surrogate pair
                    if (IsValidIdentifierStart(name, startIndex, index - startIndex))
                    {
                        startIndex = index;
                        isValid = true;
                    }
                }
            }

            return isValid;
        }

        /// <summary>
        /// Determines if the character(s) at the given index is a valid identifier part
        /// </summary>
        /// <param name="text">potential identifier string</param>
        /// <param name="index">index of the starting character</param>
        /// <returns>true if the character at the given position is a valid identifier part</returns>
        static bool IsValidIdentifierPart(string text, int index)
        {
            return IsValidIdentifierPart(text, ref index);
        }

        /// <summary>
        /// Determines if the character(s) at the given index is a valid identifier part,
        /// and adjust the index to point to the following character
        /// </summary>
        /// <param name="name">potential identifier string</param>
        /// <param name="startIndex">index of the starting character on entry; index of the NEXT character on exit</param>
        /// <returns>true if the character at the given position is a valid identifier part</returns>
        static bool IsValidIdentifierPart(string name, ref int startIndex)
        {
            var isValid = false;

            // pull the first character from the string, which may be an escape character
            if (name != null && startIndex < name.Length)
            {
                var index = startIndex;
                char ch = name[index];
                if (ch == '\\')
                {
                    // unescape the escape sequence(s)
                    // might use two sequences if they are an escaped surrogate pair
                    name = PeekUnicodeEscape(name, ref index);
                    if (name != null && IsValidIdentifierPart(name, 0, name.Length))
                    {
                        startIndex = index;
                        isValid = true;
                    }
                }
                else
                {
                    if (0xd800 <= ch && ch <= 0xdbff)
                    {
                        // high-order surrogate
                        ch = name[++index];
                        if (0xdc00 <= ch && ch <= 0xdfff)
                        {
                            // surrogate pair
                            ++index;
                        }
                    }
                    else
                    {
                        // just the one character
                        ++index;
                    }

                    // no escape sequence, but might be a surrogate pair
                    if (IsValidIdentifierPart(name, startIndex, index - startIndex))
                    {
                        startIndex = index;
                        isValid = true;
                    }
                }
            }

            return isValid;
        }

        static bool IsValidIdentifierStart(string text, int index, int length)
        {
            if (text != null)
            {
                var letter = text[index];
                if (length == 1 && (('a' <= letter && letter <= 'z')
                    || ('A' <= letter && letter <= 'Z')
                    || letter == '_'
                    || letter == '$'
                    || letter == '#'
                    || letter == '\ufffd'))
                {
                    return true;
                }
                else
                {
                    switch (CharUnicodeInfo.GetUnicodeCategory(text, index))
                    {
                        case UnicodeCategory.UppercaseLetter:
                        case UnicodeCategory.LowercaseLetter:
                        case UnicodeCategory.TitlecaseLetter:
                        case UnicodeCategory.ModifierLetter:
                        case UnicodeCategory.OtherLetter:
                        case UnicodeCategory.LetterNumber:
                            return true;
                    }
                }
            }

            return false;
        }

        static bool IsValidIdentifierPart(string text, int index, int length)
        {
            if (text != null)
            {
                var letter = text[index];
                if (length == 1 && (('a' <= letter && letter <= 'z')
                    || ('A' <= letter && letter <= 'Z')
                    || ('0' <= letter && letter <= '9')
                    || letter == '_'
                    || letter == '$'
                    || letter == '\u200c'
                    || letter == '\u200d'
                    || letter == '\ufffd'))
                {
                    return true;
                }
                else
                {
                    switch (CharUnicodeInfo.GetUnicodeCategory(text, index))
                    {
                        case UnicodeCategory.UppercaseLetter:
                        case UnicodeCategory.LowercaseLetter:
                        case UnicodeCategory.TitlecaseLetter:
                        case UnicodeCategory.ModifierLetter:
                        case UnicodeCategory.OtherLetter:
                        case UnicodeCategory.LetterNumber:
                        case UnicodeCategory.NonSpacingMark:
                        case UnicodeCategory.SpacingCombiningMark:
                        case UnicodeCategory.DecimalDigitNumber:
                        case UnicodeCategory.ConnectorPunctuation:
                            return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Determines whether the first character, surrogate pair, or escape sequence of the 
        /// given string represents a valid identifier part.
        /// </summary>
        /// <param name="text">one or more characters</param>
        /// <returns>true if the first character represents a valid identifier part.</returns>
        public static bool StartsWithValidIdentifierPart(string text)
        {
            var isValid = false;

            // pull the first character from the string, which may be an escape character
            if (text != null)
            {
                char ch = text[0];
                if (ch == '\\')
                {
                    var index = 0;
                    var unicode = PeekUnicodeEscape(text, ref index);
                    isValid = (unicode != null && IsValidIdentifierPart(unicode, 0, unicode.Length));
                }
                else
                {
                    isValid = IsValidIdentifierPart(text, 0, 1);
                }
            }

            return isValid;
        }

        /// <summary>
        /// Determines if the given charater is a valid identifier part.
        /// Does not work with extended UNICODE character
        /// </summary>
        /// <param name="letter">UTF-16 codepoint to test</param>
        /// <returns>true if the given character is a valid identifier part</returns>
        public static bool IsValidIdentifierPart(char letter)
        {
            var index = (int)letter;
            if (index < 256)
            {
                return validIdentifierPartMap[index];
            }

            return IsValidIdentifierPart(new string(letter, 1), 0);
        }

        /// <summary>
        /// Returns true of the given token is an assignment operator
        /// </summary>
        /// <param name="token">token to test</param>
        /// <returns>true if the token is an assignment operator</returns>
        public static bool IsAssignmentOperator(JSToken token)
        {
            return JSToken.Assign <= token && token <= JSToken.LastAssign;
        }

        /// <summary>
        /// Returns true if the given token is a right-associative operator
        /// </summary>
        /// <param name="token">token to test</param>
        /// <returns>true if right-associative</returns>
        public static bool IsRightAssociativeOperator(JSToken token)
        {
            return (JSToken.Assign <= token && token <= JSToken.ConditionalIf)
                || token == JSToken.Exponent;
        }

        /// <summary>
        /// determines whether a string is a replacement token value
        /// </summary>
        /// <param name="value">string to tet</param>
        /// <returns>true if in the format %name(.name)*(:ident)?%</returns>
        public static bool IsReplacementToken(string value)
        {
            // must be in the format %name(.name)*(:name)?%
            // so must start and end with a percent with at least ONE character between,
            // and can't start or end with a period. name can be identifier part character,
            // or a hyphen. Can optionally be followed by a colon and a name.
            if (value != null
                && value.Length > 2
                && value.StartsWith("%", StringComparison.Ordinal)
                && value.EndsWith("%", StringComparison.Ordinal)
                && value[1] != '.'
                && value[value.Length - 2] != '.')
            {
                var ndx = 1;
                char ch = '\0';
                for (; ndx < value.Length - 1; ++ndx)
                {
                    if (value[ndx] == ':' && ch != '.')
                    {
                        ch = ':';
                        break;
                    }

                    ch = value[ndx];
                    if (!IsValidIdentifierPart(ch)
                        && ch != '-'
                        && ch != '.')
                    {
                        // invalid -- not a replacement token
                        return false;
                    }
                }

                if (ch == ':')
                {
                    // must be followed by an identifier
                    for (; ndx < value.Length - 1; ++ndx)
                    {
                        if (!IsValidIdentifierPart(value[ndx]))
                        {
                            // invalid -- not a replacement token
                            return false;
                        }
                    }
                }

                // if we're at the last character, we're good (we already know it's a %)
                if (ndx == value.Length - 1)
                {
                    return true;
                }
            }

            // nope
            return false;
        }

        #endregion

        #region Safe identifier helper methods

        // assumes all unicode characters in the string -- NO escape sequences
        public static bool IsSafeIdentifier(string name)
        {
            bool isValid = false;
            if (!string.IsNullOrEmpty(name))
            {
                if (IsSafeIdentifierStart(name[0]))
                {
                    // loop through all the rest
                    for (int ndx = 1; ndx < name.Length; ++ndx)
                    {
                        char ch = name[ndx];
                        if (!IsSafeIdentifierPart(ch))
                        {
                            // fail!
                            return false;
                        }
                    }

                    // if we get here, everything is okay
                    isValid = true;
                }
            }

            return isValid;
        }

        // unescaped unicode characters.
        // the same as the "IsValid" method, except various browsers have problems with some
        // of the Unicode characters in the ModifierLetter, OtherLetter, and LetterNumber categories.
        public static bool IsSafeIdentifierStart(char letter)
        {
            if (('a' <= letter && letter <= 'z') || ('A' <= letter && letter <= 'Z') || letter == '_' || letter == '$')
            {
                // good
                return true;
            }

            return false;
        }

        // unescaped unicode characters.
        // the same as the "IsValid" method, except various browsers have problems with some
        // of the Unicode characters in the ModifierLetter, OtherLetter, LetterNumber,
        // NonSpacingMark, SpacingCombiningMark, DecimalDigitNumber, and ConnectorPunctuation categories.
        public static bool IsSafeIdentifierPart(char letter)
        {
            // look for valid ranges
            if (('a' <= letter && letter <= 'z')
                || ('A' <= letter && letter <= 'Z')
                || ('0' <= letter && letter <= '9')
                || letter == '_'
                || letter == '$')
            {
                return true;
            }

            return false;
        }

        #endregion

        #region event methods

        void OnGlobalDefine(string name)
        {
            if (GlobalDefine != null)
            {
                GlobalDefine(this, new GlobalDefineEventArgs() { Name = name });
            }
        }

        void OnNewModule(string newModule)
        {
            if (NewModule != null)
            {
                NewModule(this, new NewModuleEventArgs { Module = newModule });
            }
        }

        #endregion

        #region private scan methods

        JSToken ScanLineTerminator(char ch)
        {
            // line terminator
            var token = JSToken.EndOfLine;
            if (inConditionalComment && inSingleLineComment)
            {
                // if we are in a single-line conditional comment, then we want
                // to return the end of comment token WITHOUT moving past the end of line 
                // characters
                token = JSToken.ConditionalCommentEnd;
                inConditionalComment = inSingleLineComment = false;
            }
            else
            {
                ++currentPosition;
                if (ch == '\r')
                {
                    // \r\n is a valid SINGLE line-terminator. So if the \r is
                    // followed by a \n, we only want to process a single line terminator.
                    if (GetChar(currentPosition) == '\n')
                    {
                        ++currentPosition;
                        CurrentLine++;
                    }
                }
                else
                {
                    CurrentLine++;
                }

                StartLinePosition = currentPosition;

                // keep multiple line terminators together in a single token.
                // so get the current character after this last line terminator
                // and then keep looping until we hit something that isn't one.
                while ((ch = GetChar(currentPosition)) == '\r' || ch == '\n' || ch == '\u2028' || ch == '\u2029')
                {
                    if (ch == '\r')
                    {
                        // skip over the \r and if the next one is an \n skip over it, too
                        if (GetChar(++currentPosition) == '\n')
                        {
                            // increment the line number and reset the start position
                            CurrentLine++;
                            ++currentPosition;
                        }
                    }
                    else
                    {
                        // skip over any other non-\r character
                        ++currentPosition;

                        // increment the line number and reset the start position
                        CurrentLine++;
                    }

                    StartLinePosition = currentPosition;
                }
            }

            // if we WERE in a single-line comment, we aren't anymore!
            inSingleLineComment = false;
            return token;
        }

        /// <summary>
        /// Scan an identifier starting at the current location, escaping any escape sequences
        /// </summary>
        /// <returns>token scanned</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA1502:AvoidExcessiveComplexity")]
        JSToken ScanIdentifier(bool possibleTemplateLiteral)
        {
            // scan start character
            var isValid = false;
            var runStart = currentPosition;
            var index = currentPosition;
            char ch = GetChar(index);
            if (ch == '\\')
            {
                // unescape the escape sequence(s)
                // might use two sequences if they are an escaped surrogate pair
                mightBeKeyword = false;
                var unescaped = PeekUnicodeEscape(sourceCode, ref index);
                if (unescaped != null && IsValidIdentifierStart(unescaped, 0, unescaped.Length))
                {
                    // add the unescaped value(s) to the string builder and update the
                    // position for the next run
                    identifier.Append(unescaped);
                    runStart = currentPosition = index;
                    isValid = true;
                }
            }
            else
            {
                if (0xd800 <= ch && ch <= 0xdbff)
                {
                    // high-order surrogate
                    mightBeKeyword = false;
                    ch = GetChar(++index);
                    if (0xdc00 <= ch && ch <= 0xdfff)
                    {
                        // surrogate pair
                        ++index;
                    }
                }
                else
                {
                    // just the one character
                    ++index;
                }

                // no escape sequence, but might be a surrogate pair
                if (IsValidIdentifierStart(sourceCode, currentPosition, index - currentPosition))
                {
                    mightBeKeyword = mightBeKeyword && 'a' <= ch && ch <= 'z';
                    currentPosition = index;
                    isValid = true;
                }
            }

            if (isValid)
            {
                // loop until we don't find a valid part character
                ch = GetChar(currentPosition);
                while (ch != '\0')
                {
                    index = currentPosition;
                    if (ch == '\\')
                    {
                        // unescape the escape sequence(s)
                        // might use two sequences if they are an escaped surrogate pair
                        mightBeKeyword = false;
                        var unescaped = PeekUnicodeEscape(sourceCode, ref index);
                        if (unescaped == null || !IsValidIdentifierPart(unescaped, 0, unescaped.Length))
                        {
                            break;
                        }

                        // if there was a previous run, add it to the builder first
                        if (currentPosition > runStart)
                        {
                            identifier.Append(sourceCode, runStart, currentPosition - runStart);
                        }

                        identifier.Append(unescaped);
                        runStart = currentPosition = index;
                    }
                    else
                    {
                        if (0xd800 <= ch && ch <= 0xdbff)
                        {
                            // high-order surrogate
                            mightBeKeyword = false;
                            ch = GetChar(++index);
                            if (0xdc00 <= ch && ch <= 0xdfff)
                            {
                                // surrogate pair
                                ++index;
                            }
                        }
                        else
                        {
                            // just the one character
                            ++index;
                        }

                        // no escape sequence, but might be a surrogate pair
                        if (!IsValidIdentifierPart(sourceCode, currentPosition, index - currentPosition))
                        {
                            break;
                        }

                        mightBeKeyword = mightBeKeyword && 'a' <= ch && ch <= 'z';
                        currentPosition = index;
                    }

                    ch = GetChar(currentPosition);
                }

                if (AllowEmbeddedAspNetBlocks && CheckSubstring(currentPosition, "<%="))
                {
                    // the identifier has an ASP.NET <%= ... %> block as part of it.
                    // move the current position to the opening % character and call 
                    // the method that will parse it from there.
                    ++currentPosition;
                    ScanAspNetBlock();
                }

                if (identifier.Length > 0 && currentPosition - runStart > 0)
                {
                    identifier.Append(sourceCode, runStart, currentPosition - runStart);
                }
            }

            if (possibleTemplateLiteral && isValid && GetChar(currentPosition) == '`')
            {
                // identifier is valid and followed immediately by a back-quote.
                // this is a template literal starting with a function!
                return ScanTemplateLiteral('`');
            }

            return isValid ? JSToken.Identifier : JSToken.Error;
        }

        JSToken ScanKeyword(JSKeyword keyword)
        {
            // scan an identifier
            var token = ScanIdentifier(true);

            // if we found one and from the first character we think we COULD be
            // a keyword, AND we didn't encounter any escapes while reading said identifier...
            if (keyword != null && mightBeKeyword)
            {
                if (token == JSToken.Identifier)
                {
                    // walk the keyword list to find a possible match
                    var keywordToken = keyword.GetKeyword(sourceCode, CurrentToken.StartPosition, currentPosition - CurrentToken.StartPosition);

                    // you can do `new.target.prototype`
                    if (keywordToken == JSToken.New && GetChar(currentPosition) == '.')
                        return token;

                    return keywordToken;
                }
                else if (token == JSToken.TemplateLiteral)
                {
                    // wait a minute -- let's just make sure that lookup isn't a keyword
                    var ndxBackquote = sourceCode.IndexOf('`', CurrentToken.StartPosition);
                    var newToken = keyword.GetKeyword(sourceCode, CurrentToken.StartPosition, ndxBackquote - CurrentToken.StartPosition);
                    if (newToken != JSToken.Identifier)
                    {
                        // no, use the keyword token and back up to the opening backquote
                        token = newToken;
                        currentPosition = ndxBackquote;
                    }
                }
            }

            return token;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA1502:AvoidExcessiveComplexity")]
        JSToken ScanNumber(char leadChar)
        {
            bool noMoreDot = '.' == leadChar;
            JSToken token = noMoreDot ? JSToken.NumericLiteral : JSToken.IntegerLiteral;
            bool exponent = false;
            char c;
            LiteralHasIssues = false;

            if ('0' == leadChar)
            {
                // c is now the character AFTER the leading zero
                c = GetChar(currentPosition);
                if ('x' == c || 'X' == c)
                {
                    if (JSScanner.IsHexDigit(GetChar(currentPosition + 1)))
                    {
                        while (JSScanner.IsHexDigit(c = GetChar(++currentPosition)) || c == '_')
                        {
                            // empty
                        }
                    }

                    return CheckForNumericBadEnding(token);
                }

                if ('b' == c || 'B' == c)
                {
                    // ES6 binary literal?
                    c = GetChar(currentPosition + 1);
                    if (c == '1' || c == '0')
                    {
                        while ('0' == (c = GetChar(++currentPosition)) || c == '1' || c == '_')
                        {
                            // iterator handled in the condition
                        }
                    }

                    return CheckForNumericBadEnding(token);
                }

                if ('o' == c || 'O' == c)
                {
                    // ES6 octal literal?
                    c = GetChar(currentPosition + 1);
                    if ('0' <= c && c <= '7')
                    {
                        while (('0' <= (c = GetChar(++currentPosition)) && c <= '7') || c == '_')
                        {
                            // iterator handled in the condition
                        }
                    }

                    return CheckForNumericBadEnding(token);
                }

                if ('0' <= c && c <= '7')
                {
                    // this is a zero followed by a digit between 0 and 7.
                    // This could be interpreted as an octal literal, which isn't strictly supported.
                    while ('0' <= c && c <= '7')
                    {
                        c = GetChar(++currentPosition);
                    }

                    // bad octal?
                    if (IsDigit(c) && '7' < c)
                    {
                        // bad octal. Skip any other digits, throw an error, mark it has having issues
                        LiteralHasIssues = true;
                        while ('0' <= c && c <= '9')
                        {
                            c = GetChar(++currentPosition);
                        }

                        HandleError(JSError.BadNumericLiteral);
                    }

                    // return the integer token with issues, which should cause it to be output
                    // as-is and not combined with other literals or anything.
                    LiteralHasIssues = true;
                    HandleError(JSError.OctalLiteralsDeprecated);
                    return token;
                }

                if (c != 'e' && c != 'E' && IsValidIdentifierStart(sourceCode, currentPosition))
                {
                    // invalid for an integer (in this case '0') the be followed by
                    // an identifier part. The 'e' is okay, though, because that will
                    // be the exponent part.
                    // we know the 0 and the next character are both invalid, so skip them and 
                    // anything else after it that's identifier-like, and throw an error.
                    return CheckForNumericBadEnding(token);
                }
            }

            for (; ; )
            {
                c = GetChar(currentPosition);
                if (!IsDigit(c))
                {
                    if ('.' == c)
                    {
                        if (GetChar(currentPosition - 1) == '_')
                        {
                            LiteralHasIssues = true;
                            HandleError(JSError.BadNumericLiteral);
                        }

                        if (noMoreDot)
                            break;

                        noMoreDot = true;
                        token = JSToken.NumericLiteral;
                    }
                    else if ('_' == c)
                    {
                        c = GetChar(currentPosition - 1);
                        // report multiple adjacent separators as an error, although we can handle it so still minify
                        if (c == '_')
                        {
                            LiteralHasIssues = true;
                            HandleError(JSError.BadNumericLiteral);
                        }
                        else if (c == '.')
                        {
                            LiteralHasIssues = true;
                            HandleError(JSError.BadNumericLiteral);
                        }
                    }
                    else if ('e' == c || 'E' == c)
                    {
                        if (exponent)
                        {
                            break;
                        }

                        exponent = noMoreDot = true;
                        token = JSToken.NumericLiteral;
                    }
                    else if ('+' == c || '-' == c)
                    {
                        char e = GetChar(currentPosition - 1);
                        if ('e' != e && 'E' != e)
                        {
                            break;
                        }
                    }
                    else if ('n' == c)
                    {
                        // TODO: handle decimals
                        token = JSToken.BigIntLiteral;
                    }
                    else
                    {
                        if (GetChar(currentPosition - 1) == '_')
                        {
                            LiteralHasIssues = true;
                            HandleError(JSError.BadNumericLiteral);
                        }
                        break;
                    }
                }

                currentPosition++;
            }

            // get the last character of the number
            c = GetChar(currentPosition - 1);
            if ('+' == c || '-' == c)
            {
                // if it's a + or -, then it's not part of the number; back it up one
                currentPosition--;
                c = GetChar(currentPosition - 1);
            }

            if ('e' == c || 'E' == c)
            {
                // if it's an e, it's not part of the number; back it up one
                currentPosition--;
                c = GetChar(currentPosition - 1);
            }

            if (token == JSToken.NumericLiteral && c == '.')
            {
                // if we thought this was a numeric value and not an integer, but the last
                // value was the decimal point, treat it as an integer.
                token = JSToken.IntegerLiteral;
            }

            // it is invalid for a numeric literal to be immediately followed by another
            // digit or an identifier start character. So check for those and return an
            // invalid numeric literal if true.
            return CheckForNumericBadEnding(token);
        }

        JSToken ScanReplacementToken()
        {
            // save this information in case we need to restore the token
            // if we don't actually have a valid replacement here.
            int startingPosition = currentPosition;
            int startingLine = CurrentLine;
            int startingLinePosition = StartLinePosition;

            // current position should be the character following the %.
            // We should have name(.name)*% at the current position for it to be a replacement token.
            // let's just simplify this and say no escape sequences or surrogate pairs allowed; just simple identifier PART
            // characters separated by periods.
            var ch = GetChar(currentPosition);

            // the name portion should not START with a period
            if (ch != '.')
            {
                // identifier parts, hyphens, and periods are allowed as the token name.
                while (IsValidIdentifierPart(ch) || ch == '.' || ch == '-')
                {
                    ch = GetChar(++currentPosition);
                }

                // the token name can optionally be followed by a colon
                // and a replacement fallback class (another identifier),
                // so if there's a colon now, scan past it and the 
                // following identifier (if any).
                if (ch == ':')
                {
                    ch = GetChar(++currentPosition);
                    while (IsValidIdentifierPart(ch))
                    {
                        ch = GetChar(++currentPosition);
                    }
                }
            }

            if (ch == '%'
                && currentPosition > startingPosition + 1
                && GetChar(currentPosition - 1) != '.')
            {
                // must have found AT LEAST one character for the name 
                // and it should not have ended with a period.
                ++currentPosition;
                return JSToken.ReplacementToken;
            }

            // reset and return what we currently have. apparently it's not a replacement token
            currentPosition = startingPosition;
            CurrentLine = startingLine;
            StartLinePosition = startingLinePosition;
            return CurrentToken.Token;
        }

        JSToken ScanRegExp()
        {
            // save this information in case we need to restore the token
            // because we don't actually have a regular expression here.
            int startingPosition = currentPosition;
            int startingLine = CurrentLine;
            int startingLinePosition = StartLinePosition;

            bool isEscape = false;
            bool isInSet = false;
            char c;
            while (!IsEndLineOrEOF(c = GetChar(currentPosition++), 0))
            {
                if (isEscape)
                {
                    isEscape = false;
                }
                else if (c == '\\')
                {
                    isEscape = true;
                }
                else if (c == '[')
                {
                    isInSet = true;
                }
                else if (isInSet)
                {
                    if (c == ']')
                    {
                        isInSet = false;
                    }
                }
                else if (c == '/')
                {
                    if (startingPosition == currentPosition)
                    {
                        // nothing; bail
                        break;
                    }

                    // pull in the flags, which are simply letter characters
                    while (!IsEndLineOrEOF(c = GetChar(currentPosition), 0)
                        && IsValidIdentifierPart(c))
                    {
                        ++currentPosition;
                    }

                    // found one! 
                    return JSToken.RegularExpression;
                }
            }

            // reset and return what we currently have. apparently it's not a reg exp
            currentPosition = startingPosition;
            CurrentLine = startingLine;
            StartLinePosition = startingLinePosition;
            return CurrentToken.Token;
        }

        /// <summary>
        /// Scans for the end of an Asp.Net block.
        ///  On exit this.currentPos will be at the next char to scan after the asp.net block.
        /// </summary>
        JSToken ScanAspNetBlock()
        {
            // assume we find an asp.net block
            var tokenType = JSToken.AspNetBlock;

            // the current position is the % that opens the <%.
            // advance to the next character and save it because we will want 
            // to know whether it's an equals-sign later
            var thirdChar = GetChar(++currentPosition);

            // advance to the next character
            ++currentPosition;

            // loop until we find a > with a % before it (%>)
            while (!(this.GetChar(this.currentPosition - 1) == '%' &&
                     this.GetChar(this.currentPosition) == '>') ||
                     IsEndOfFile)
            {
                this.currentPosition++;
            }

            // we should be at the > of the %> right now.
            // set the end point of this token
            CurrentToken.EndPosition = currentPosition + 1;
            CurrentToken.EndLineNumber = CurrentLine;
            CurrentToken.EndLinePosition = StartLinePosition;

            // see if we found an unterminated asp.net block
            if (IsEndOfFile)
            {
                HandleError(JSError.UnterminatedAspNetBlock);
            }
            else
            {
                // Eat the last >.
                this.currentPosition++;

                if (thirdChar == '=')
                {
                    // this is a <%= ... %> token.
                    // we're going to treat this like an identifier
                    tokenType = JSToken.Identifier;

                    // now, if the next character is an identifier part
                    // then skip to the end of the identifier. And if this is
                    // another <%= then skip to the end (%>)
                    if (IsValidIdentifierPart(sourceCode, currentPosition)
                        || CheckSubstring(currentPosition, "<%="))
                    {
                        // and do it however many times we need
                        while (true)
                        {
                            if (IsValidIdentifierPart(sourceCode, ref currentPosition))
                            {
                                // skip to the end of the identifier part
                                while (IsValidIdentifierPart(sourceCode, ref currentPosition))
                                {
                                    // loop
                                }

                                // when we get here, the current position is the first
                                // character that ISN"T an identifier-part. That means everything 
                                // UP TO this point must have been on the 
                                // same line, so we only need to update the position
                                CurrentToken.EndPosition = currentPosition;
                            }
                            else if (CheckSubstring(currentPosition, "<%="))
                            {
                                // skip forward four characters -- the minimum position
                                // for the closing %>
                                currentPosition += 4;

                                // and keep looping until we find it
                                while (!(this.GetChar(this.currentPosition - 1) == '%' &&
                                         this.GetChar(this.currentPosition) == '>') ||
                                         IsEndOfFile)
                                {
                                    this.currentPosition++;
                                }

                                // update the end of the token
                                CurrentToken.EndPosition = currentPosition + 1;
                                CurrentToken.EndLineNumber = CurrentLine;
                                CurrentToken.EndLinePosition = StartLinePosition;

                                // we should be at the > of the %> right now.
                                // see if we found an unterminated asp.net block
                                if (IsEndOfFile)
                                {
                                    HandleError(JSError.UnterminatedAspNetBlock);
                                }
                                else
                                {
                                    // skip the > and go around another time
                                    ++currentPosition;
                                }
                            }
                            else
                            {
                                // neither an identifer part nor another <%= sequence,
                                // so we're done here
                                break;
                            }
                        }
                    }
                }
            }

            return tokenType;
        }

        //--------------------------------------------------------------------------------------------------
        // ScanString
        //
        //  Scan a string dealing with escape sequences.
        //  On exit this.escapedString will contain the string with all escape sequences replaced
        //  On exit this.currentPos must be at the next char to scan after the string
        //  This method wiil report an error when the string is unterminated or for a bad escape sequence
        //--------------------------------------------------------------------------------------------------
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA1502:AvoidExcessiveComplexity")]
        void ScanString(char delimiter)
        {
            int start = ++currentPosition;
            StringLiteralValue = null;
            LiteralHasIssues = false;
            StringBuilder result = null;
            try
            {
                char ch;
                while ((ch = GetChar(currentPosition++)) != delimiter)
                {
                    if (ch != '\\')
                    {
                        // this is the common non escape case
                        if (IsLineTerminator(ch, 0))
                        {
                            // TODO: we want to flag this string as unterminated *and having issues*,
                            // and then somehow output a line-break in the output to duplicate the
                            // source. However, we will need to figure out how to NOT combine the statement
                            // with the next statement. For instance:
                            //      var x = "unterminated
                            //      var y = 42;
                            // should NOT get combined to: var x="unterminated,y=42;
                            // (same for moving it inside for-statements, combining expression statements, etc.)
                            //m_literalIssues = true;
                            HandleError(JSError.UnterminatedString);

                            // back up to the start of the line terminator
                            --currentPosition;
                            if (GetChar(currentPosition - 1) == '\r')
                            {
                                --currentPosition;
                            }

                            break;
                        }

                        if ('\0' == ch)
                        {
                            // whether it's a null literal character within the string or an
                            // actual end of file, this string literal has issues....
                            LiteralHasIssues = true;

                            if (IsEndOfFile)
                            {
                                currentPosition--;
                                HandleError(JSError.UnterminatedString);
                                break;
                            }

                        }

                        if (AllowEmbeddedAspNetBlocks
                            && ch == '<'
                            && GetChar(currentPosition) == '%')
                        {
                            // start of an ASP.NET block INSIDE a string literal.
                            // just skip the entire ASP.NET block -- move forward until
                            // we find the closing %> delimiter, then we'll continue on
                            // with the next character.
                            SkipAspNetReplacement();

                            // asp.net blocks insides strings can cause issues
                            LiteralHasIssues = true;
                        }
                        else if (0xd800 <= ch && ch <= 0xdbff)
                        {
                            // high-surrogate! Make sure the next character is a low surrogate
                            // or we'll throw an error.
                            ch = GetChar(currentPosition);
                            if (0xdc00 <= ch && ch <= 0xdfff)
                            {
                                // we're good. Advance past the pair.
                                ++currentPosition;
                            }
                            else if (ch == '\\' && GetChar(currentPosition + 1) == 'u')
                            {
                                // we have a unicode escape. Start working on that escaped value.
                                if (null == result)
                                {
                                    result = StringBuilderPool.Acquire();
                                }

                                // start points to the first position that has not been written to the StringBuilder.
                                // The first time we get in here that position is the beginning of the string, after that
                                // is the character immediately following the escape sequence
                                if (currentPosition - start > 0)
                                {
                                    // append all the non escape chars to the string builder
                                    result.Append(sourceCode, start, currentPosition - start);
                                }

                                int lowSurrogate;
                                if (ScanHexSequence(currentPosition += 2, 'u', out lowSurrogate))
                                {
                                    // valid escape, so make sure the unescaped value is added to the result regardless.
                                    result.Append((char)lowSurrogate);
                                    start = currentPosition;

                                    // now make sure it's in low-surrogate range
                                    if (lowSurrogate < 0xdc00 || 0xdfff < lowSurrogate)
                                    {
                                        // not a low-surrogate
                                        LiteralHasIssues = true;
                                        HandleError(JSError.HighSurrogate);
                                    }
                                }
                                else
                                {
                                    // not a valid unicode escape sequence, so no -- we are not 
                                    // followed by a low-surrogate
                                    LiteralHasIssues = true;
                                    HandleError(JSError.HighSurrogate);
                                }
                            }
                            else
                            {
                                // not followed by a low-surrogate
                                LiteralHasIssues = true;
                                HandleError(JSError.HighSurrogate);
                            }
                        }
                        else if (0xdc00 <= ch && ch <= 0xdfff)
                        {
                            // low-surrogate by itself! This is an error, but keep going
                            LiteralHasIssues = true;
                            HandleError(JSError.LowSurrogate);
                        }
                    }
                    else
                    {
                        // ESCAPE CASE
                        var esc = 0;

                        // got an escape of some sort. Have to use the StringBuilder
                        if (null == result)
                        {
                            result = StringBuilderPool.Acquire();
                        }

                        // start points to the first position that has not been written to the StringBuilder.
                        // The first time we get in here that position is the beginning of the string, after that
                        // is the character immediately following the escape sequence
                        if (currentPosition - start - 1 > 0)
                        {
                            // append all the non escape chars to the string builder
                            result.Append(sourceCode, start, currentPosition - start - 1);
                        }

                        // state variable to be reset
                        bool seqOfThree = false;

                        ch = GetChar(currentPosition++);
                        switch (ch)
                        {
                            // line terminator crap
                            case '\r':
                                if ('\n' == GetChar(currentPosition))
                                {
                                    CurrentLine++;
                                    currentPosition++;
                                }

                                StartLinePosition = currentPosition;
                                break;

                            case '\n':
                            case '\u2028':
                            case '\u2029':
                                CurrentLine++;
                                StartLinePosition = currentPosition;
                                break;

                            // classic single char escape sequences
                            case 'b':
                                result.Append((char)8);
                                break;

                            case 't':
                                result.Append((char)9);
                                break;

                            case 'n':
                                result.Append((char)10);
                                break;

                            case 'v':
                                // \v inside strings can cause issues
                                LiteralHasIssues = true;
                                result.Append((char)11);
                                break;

                            case 'f':
                                result.Append((char)12);
                                break;

                            case 'r':
                                result.Append((char)13);
                                break;

                            case '"':
                                result.Append('"');
                                break;

                            case '\'':
                                result.Append('\'');
                                break;

                            case '\\':
                                result.Append('\\');
                                break;

                            // hexadecimal escape sequence /xHH, \uHHHH, or \u{H+}
                            case 'u':
                            case 'x':
                                string unescaped;
                                if (ScanHexEscape(ch, out unescaped))
                                {
                                    // successfully escaped the character sequence
                                    result.Append(unescaped);
                                }
                                else
                                {
                                    // wasn't valid -- keep the original and flag this as having issues
                                    result.Append(sourceCode.Substring(currentPosition - 2, 2));
                                    LiteralHasIssues = true;
                                    HandleError(JSError.BadHexEscapeSequence);
                                }
                                break;

                            case '0':
                            case '1':
                            case '2':
                            case '3':
                                seqOfThree = true;
                                esc = (ch - '0') << 6;
                                goto case '4';

                            case '4':
                            case '5':
                            case '6':
                            case '7':
                                // octal literals inside strings can cause issues
                                LiteralHasIssues = true;

                                // esc is reset at the beginning of the loop and it is used to check that we did not go through the cases 1, 2 or 3
                                if (!seqOfThree)
                                {
                                    esc = (ch - '0') << 3;
                                }

                                ch = GetChar(currentPosition++);
                                if ('0' <= ch && ch <= '7')
                                {
                                    if (seqOfThree)
                                    {
                                        esc |= (ch - '0') << 3;
                                        ch = GetChar(currentPosition++);
                                        if ('0' <= ch && ch <= '7')
                                        {
                                            esc |= ch - '0';
                                            result.Append((char)esc);
                                        }
                                        else
                                        {
                                            result.Append((char)(esc >> 3));

                                            // do not skip over this char we have to read it back
                                            --currentPosition;
                                        }
                                    }
                                    else
                                    {
                                        esc |= ch - '0';
                                        result.Append((char)esc);
                                    }
                                }
                                else
                                {
                                    if (seqOfThree)
                                    {
                                        result.Append((char)(esc >> 6));
                                    }
                                    else
                                    {
                                        result.Append((char)(esc >> 3));
                                    }

                                    // do not skip over this char we have to read it back
                                    --currentPosition;
                                }

                                HandleError(JSError.OctalLiteralsDeprecated);
                                break;

                            default:
                                // not an octal number, ignore the escape '/' and simply append the current char
                                result.Append(ch);
                                break;
                        }

                        start = currentPosition;
                    }
                }

                // update the unescaped string
                if (null != result)
                {
                    if (currentPosition - start - 1 > 0)
                    {
                        // append all the non escape chars to the string builder
                        result.Append(sourceCode, start, currentPosition - start - 1);
                    }
                    StringLiteralValue = result.ToString();
                }
                else if (currentPosition == CurrentToken.StartPosition + 1)
                {
                    // empty unterminated string!
                    StringLiteralValue = string.Empty;
                }
                else
                {
                    // might be an unterminated string, so make sure that last character is the terminator
                    int numDelimiters = (GetChar(currentPosition - 1) == delimiter ? 2 : 1);
                    StringLiteralValue = sourceCode.Substring(CurrentToken.StartPosition + 1, currentPosition - CurrentToken.StartPosition - numDelimiters);
                }
            }
            finally
            {
                result.Release();
            }
        }

        bool ScanHexEscape(char hexType, out string unescaped)
        {
            // current character should be the first character AFTER the "\u" pair
            int numeric;

            // save this in case there's an error and we back up to where we started
            var startOfDigits = currentPosition;
            var isValidHex = ScanHexSequence(startOfDigits, hexType, out numeric);
            if (isValidHex)
            {
                if (0xd800 <= numeric && numeric <= 0xdbff)
                {
                    // high surrogate! Cannot be a valid unicode character on its own -- must
                    // be followed by a low surrogate character. Get the next character. If it's 
                    // an unescaped low surrogate, use it. But if the next character is a backslash,
                    // check for U, decode the escape, and then use it. If it's not okay, return
                    // false.
                    var ch = GetChar(currentPosition);
                    if (0xdc00 <= ch && ch <= 0xdfff)
                    {
                        // skip the single low-surrogate character and return a valid two-character string
                        // using the raw numeric values for the high and low surrogate pairs.
                        // (strings internally are UTF-16)
                        ++currentPosition;
                        unescaped = new string(new[] { (char)numeric, ch });
                        return true;
                    }
                    else
                    {
                        if (ch == '\\')
                        {
                            if (GetChar(currentPosition + 1) == 'u')
                            {
                                // advance to the character AFTER the \u and recurse
                                currentPosition += 2;
                                int lowSurrogate;
                                isValidHex = ScanHexSequence(currentPosition, hexType, out lowSurrogate);
                                if (isValidHex)
                                {
                                    // return a valid two-character string using the raw numeric values 
                                    // for the high and low surrogate pairs. (strings internally are UTF-16)
                                    unescaped = new string(new[] { (char)numeric, (char)lowSurrogate });
                                    return true;
                                }
                            }
                        }

                        // not a \u escape sequence
                        // not valid to have a high surrogate that ISN'T followed by a low-surrogate!
                        // throw the error, but return true
                        HandleError(JSError.HighSurrogate);
                        LiteralHasIssues = true;
                        unescaped = new string((char)numeric, 1);
                        return true;
                    }
                }
                else if (0xdc00 <= numeric && numeric <= 0xdfff)
                {
                    // low surrogate -- shouldn't have one by itself!
                    // throw the error, but return true
                    HandleError(JSError.LowSurrogate);
                    LiteralHasIssues = true;
                    unescaped = new string((char)numeric, 1);
                    return true;
                }
            }

            unescaped = isValidHex ? char.ConvertFromUtf32(numeric) : null;
            return isValidHex;
        }

        bool ScanHexSequence(int startOfDigits, char hexType, out int accumulator)
        {
            // current character should be the first character AFTER the "\u" pair
            var isValidHex = true;

            // how many digits we parse depends on the type. x = 2 digits, u = 4 digits.
            // UNLESS this is a type 'u' followed by a left brace. Then it's between 1 and 6.
            int digits = hexType == 'x' ? 2 : 4;
            if (hexType == 'u' && GetChar(currentPosition) == '{')
            {
                ++currentPosition;
                digits = 6;
            }

            accumulator = 0;
            var ch = GetChar(currentPosition);
            while (currentPosition - startOfDigits < digits && IsHexDigit(ch))
            {
                if (IsDigit(ch))
                {
                    accumulator = accumulator << 4 | (ch - '0');
                }
                else if ('A' <= ch && ch <= 'F')
                {
                    accumulator = accumulator << 4 | (ch - 'A' + 10);
                }
                else if ('a' <= ch && ch <= 'f')
                {
                    accumulator = accumulator << 4 | (ch - 'a' + 10);
                }

                ch = GetChar(++currentPosition);
            }

            // if we didn't find any digits at all, or
            // if we didn't find the exact number of digits we were looking for,
            // then it's an error unless we were looking for 6 and the current character is
            // a closing brace (in which case we skip the brace).
            var digitsFound = currentPosition - startOfDigits;
            if (digitsFound == 0 || (digits != 6 && digitsFound != digits) || (digits == 6 && ch != '}'))
            {
                isValidHex = false;
                currentPosition = startOfDigits;
            }
            else if (digits == 6 && ch == '}')
            {
                ++currentPosition;
            }

            return isValidHex;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA1502:AvoidExcessiveComplexity")]
        JSToken ScanTemplateLiteral(char ch)
        {
            // save the start of the current run, INCLUDING the delimiter
            StringBuilder decodedLiteral = null;
            try
            {
                var startOfRun = CurrentToken.StartPosition;

                // if the first character is the open delimiter, skip past it now
                if (ch == '`')
                {
                    ++currentPosition;
                }

                // get the current character and get the next character
                ch = GetChar(currentPosition);
                while (ch != '\0' && ch != '`')
                {
                    if (ch == '$')
                    {
                        // is this the start of a substitution?
                        // if so, then it's the end of the head. Otherwise it's just
                        // a simpe dollar-sign.
                        if (GetChar(currentPosition + 1) == '{')
                        {
                            currentPosition += 2;
                            break;
                        }
                    }
                    else if (ch == '\\')
                    {
                        // escape sequence. If we haven't already, create a string builder
                        if (decodedLiteral == null)
                        {
                            decodedLiteral = StringBuilderPool.Acquire();
                        }

                        // append the previous run of unescaped characters
                        if (currentPosition > startOfRun)
                        {
                            decodedLiteral.Append(sourceCode, startOfRun, currentPosition - startOfRun);
                        }

                        // advance past the slash and get the next escaped character
                        ch = GetChar(++currentPosition);
                        switch (ch)
                        {
                            default:
                            case '\'':
                            case '"':
                                // just the escaped character
                                decodedLiteral.Append(ch);
                                break;

                            case '\\':
                                // backslash
                                decodedLiteral.Append("\\\\");
                                break;

                            case '`':
                                // backtick
                                decodedLiteral.Append("\\`");
                                break;

                            case '$':
                                // dollar sign
                                decodedLiteral.Append("\\$");
                                break;

                            case 'b':
                                // backspace
                                decodedLiteral.Append('\b');
                                break;

                            case 'f':
                                // form feed
                                decodedLiteral.Append('\f');
                                break;

                            case 'n':
                                // line feed
                                decodedLiteral.Append('\n');
                                break;

                            case 'r':
                                // carriage return
                                decodedLiteral.Append('\r');
                                break;

                            case 't':
                                // tab
                                decodedLiteral.Append('\t');
                                break;

                            case 'v':
                                // vertical tab
                                // don't need to special-case this as it's ES6 syntax and we don't have
                                // to worry about older browsers.
                                decodedLiteral.Append('\v');
                                break;

                            case '0':
                                // null character
                                decodedLiteral.Append('\0');
                                break;

                            case '\r':
                                // line continuation - don't add anything to the decoded string.
                                // carriage return might be followed by a \n, which we will 
                                // treat together as a single line terminator.
                                if (GetChar(currentPosition + 1) == '\n')
                                {
                                    CurrentLine++;
                                    currentPosition++;
                                }
                                StartLinePosition = currentPosition;
                                break;
                            case '\n':
                            case '\u2028':
                            case '\u2029':
                                // advance the line number, but the escaped line terminator is not part
                                // of the decoded string
                                CurrentLine++;
                                StartLinePosition = currentPosition;
                                break;

                            case 'x':
                            case 'u':
                                // unicode escape. four hex digits unless enclosed in curly-braces, in which
                                // case it can be any number of hex digits.
                                string unescaped;
                                ++currentPosition;
                                if (ScanHexEscape(ch, out unescaped))
                                {
                                    decodedLiteral.Append(unescaped);
                                }
                                else
                                {
                                    // wasn't valid -- keep the original and flag this as having issues
                                    decodedLiteral.Append(sourceCode.Substring(currentPosition - 2, 2));
                                    LiteralHasIssues = true;
                                    HandleError(JSError.BadHexEscapeSequence);
                                }
                                --currentPosition;
                                break;
                        }

                        // save the start of the next run (if any)
                        startOfRun = currentPosition + 1;
                    }
                    else if (IsLineTerminator(ch, 1))
                    {
                        // we'll just keep this as part of the unescaped run
                        ++CurrentLine;
                        StartLinePosition = currentPosition + 1;
                    }

                    // keep going
                    ch = GetChar(++currentPosition);
                }

                if (ch == '`')
                {
                    ++currentPosition;
                }

                // if we were unescaping the string, pull in the last run and save the decoded string
                if (decodedLiteral != null)
                {
                    if (currentPosition > startOfRun)
                    {
                        decodedLiteral.Append(sourceCode, startOfRun, currentPosition - startOfRun);
                    }

                    StringLiteralValue = decodedLiteral.ToString();
                }
                else
                {
                    StringLiteralValue = sourceCode.Substring(CurrentToken.StartPosition, currentPosition - CurrentToken.StartPosition);
                }
            }
            finally
            {
                decodedLiteral.Release();
            }

            return JSToken.TemplateLiteral;
        }

        #endregion

        #region private Skip methods

        void SkipAspNetReplacement()
        {
            // the current position is on the % of the opening delimiter, so
            // advance the pointer forward to the first character AFTER the opening
            // delimiter, then keep skipping
            // forward until we find the closing %>. Be sure to set the current pointer
            // to the NEXT character AFTER the > when we find it.
            ++currentPosition;

            char ch;
            while ((ch = GetChar(currentPosition++)) != '\0' || !IsEndOfFile)
            {
                if (ch == '%'
                    && GetChar(currentPosition) == '>')
                {
                    // found the closing delimiter -- the current position in on the >
                    // so we need to advance to the next character and break out of the loop
                    ++currentPosition;
                    break;
                }
            }
        }

        void SkipSingleLineComment()
        {
            // skip up to the terminator, but don't include them.
            // the single-line comment does NOT include the line terminator!
            SkipToEndOfLine();

            // no longer in a single-line comment
            inSingleLineComment = false;

            // fix up the end of the token
            CurrentToken.EndPosition = currentPosition;
            CurrentToken.EndLinePosition = StartLinePosition;
            CurrentToken.EndLineNumber = CurrentLine;
        }

        void SkipToEndOfLine()
        {
            var c = GetChar(currentPosition);
            while (c != 0
                && c != '\n'
                && c != '\r'
                && c != '\x2028'
                && c != '\x2029')
            {
                c = GetChar(++currentPosition);
            }
        }

        /// <summary>
        /// skip to either the end of the line or the comment, whichever comes first, but
        /// DON'T consume either of them.
        /// </summary>
        void SkipToEndOfLineOrComment()
        {
            if (inSingleLineComment)
            {
                // single line comments are easy; the end of the line IS the end of the comment
                SkipToEndOfLine();
            }
            else
            {
                // multiline comments stop at the end of line OR the closing */, whichever comes first
                var c = GetChar(currentPosition);
                while (c != 0
                    && c != '\n'
                    && c != '\r'
                    && c != '\x2028'
                    && c != '\x2029')
                {
                    if (c == '*' && GetChar(currentPosition + 1) == '/')
                    {
                        break;
                    }

                    c = GetChar(++currentPosition);
                }
            }
        }

        void SkipOneLineTerminator()
        {
            var c = GetChar(currentPosition);
            if (c == '\r')
            {
                // skip over the \r; and if it's followed by a \n, skip it, too
                if (GetChar(++currentPosition) == '\n')
                {
                    ++currentPosition;
                }

                CurrentLine++;
                StartLinePosition = currentPosition;
                inSingleLineComment = false;
            }
            else if (c == '\n'
                || c == '\x2028'
                || c == '\x2029')
            {
                // skip over the single line-feed character
                ++currentPosition;

                CurrentLine++;
                StartLinePosition = currentPosition;
                inSingleLineComment = false;
            }
        }

        void SkipMultilineComment()
        {
            for (; ; )
            {
                char c = GetChar(currentPosition);
                while ('*' == c)
                {
                    c = GetChar(++currentPosition);
                    if ('/' == c)
                    {
                        // get past the trailing slash
                        currentPosition++;

                        // no longer in a multiline comment; fix up the end of the current token.
                        inMultipleLineComment = false;
                        CurrentToken.EndPosition = currentPosition;
                        CurrentToken.EndLinePosition = StartLinePosition;
                        CurrentToken.EndLineNumber = CurrentLine;
                        return;
                    }

                    if ('\0' == c)
                    {
                        break;
                    }

                    if (IsLineTerminator(c, 1))
                    {
                        c = GetChar(++currentPosition);
                        CurrentLine++;
                        StartLinePosition = currentPosition + 1;
                    }
                }

                if ('\0' == c && IsEndOfFile)
                {
                    break;
                }

                if (IsLineTerminator(c, 1))
                {
                    CurrentLine++;
                    StartLinePosition = currentPosition + 1;
                }

                ++currentPosition;
            }

            // if we are here we got EOF
            CurrentToken.EndPosition = currentPosition;
            CurrentToken.EndLinePosition = StartLinePosition;
            CurrentToken.EndLineNumber = CurrentLine;
            CurrentToken.HandleError(JSError.NoCommentEnd, true);
        }

        void SkipBlanks()
        {
            char c = GetChar(currentPosition);
            while (JSScanner.IsBlankSpace(c))
            {
                c = GetChar(++currentPosition);
            }
        }

        #endregion

        bool CheckSubstring(int startIndex, string target)
        {
            for (int ndx = 0; ndx < target.Length; ++ndx)
            {
                if (target[ndx] != GetChar(startIndex + ndx))
                {
                    // no match
                    return false;
                }
            }

            // if we got here, the strings match
            return true;
        }

        bool CheckCaseInsensitiveSubstring(string target)
        {
            var startIndex = currentPosition;
            for (int ndx = 0; ndx < target.Length; ++ndx)
            {
                if (target[ndx] != char.ToUpperInvariant(GetChar(startIndex + ndx)))
                {
                    // no match
                    return false;
                }
            }

            // if we got here, the strings match. Advance the current position over it
            currentPosition += target.Length;
            return true;
        }

        JSToken CheckForNumericBadEnding(JSToken token)
        {
            // it is invalid for a numeric literal to be immediately followed by another
            // digit or an identifier start character. So check for those cases and return an
            // invalid numeric literal if true.
            var isBad = false;
            char ch = GetChar(currentPosition);
            if ('0' <= ch && ch <= '9')
            {
                // we know that next character is invalid, so skip it and 
                // anything else after it that's identifier-like, and throw an error.
                ++currentPosition;
                isBad = true;
            }
            else if (IsValidIdentifierStart(sourceCode, ref currentPosition))
            {
                isBad = true;
            }

            if (isBad)
            {
                // since we know we had a bad, skip anything else after it that's identifier-like
                // and throw an error.
                while (IsValidIdentifierPart(sourceCode, ref currentPosition))
                {
                    // advance handled in the condition
                }

                LiteralHasIssues = true;
                HandleError(JSError.BadNumericLiteral);
                token = JSToken.NumericLiteral;
            }

            return token;
        }

        #region get/peek methods

        char GetChar(int index)
        {
            if (index < endPos)
            {
                return sourceCode[index];
            }

            return '\0';
        }

        static int GetHexValue(char hex)
        {
            int hexValue;
            if ('0' <= hex && hex <= '9')
            {
                hexValue = hex - '0';
            }
            else if ('a' <= hex && hex <= 'f')
            {
                hexValue = hex - 'a' + 10;
            }
            else
            {
                hexValue = hex - 'A' + 10;
            }

            return hexValue;
        }

        /// <summary>
        /// Given a string and a start index, return the integer value of the UNICODE escape
        /// sequence at the starting position. Can be '\' 'u' hex hex hex hex or '\' 'u' '{' hex+ '}'
        /// </summary>
        /// <param name="text">text to convert</param>
        /// <param name="index">starting position of the escape backslash character on entry; 
        /// index of the next character after the escape sequence on exit</param>
        /// <returns>integer conversion of the valid escape sequence, or -1 if invalid</returns>
        static int DecodeOneUnicodeEscapeSequence(string text, ref int index)
        {
            var value = -1;
            if (text != null)
            {
                if (index + 4 < text.Length && text[index] == '\\' && text[index + 1] == 'u')
                {
                    if (text[index + 2] == '{')
                    {
                        // we need to keep looping through hexadecimal digits until we
                        // find the closing brace. If we hit anything that's not hex,
                        // or if we hit the end of the string without finding the closing
                        // brace, then this is an invalid escape sequence.
                        var ch = '\0';
                        var accumulator = 0;

                        // skip past \u. The loop will start off by skipping past the {.
                        index += 2;

                        // loop as long as we have characters and we haven't hit the }.
                        while (++index < text.Length
                            && (ch = text[index]) != '}')
                        {
                            // if it's not a hex digit, bail.
                            if (!IsHexDigit(ch))
                            {
                                break;
                            }

                            // move it into the accumulator
                            accumulator = (accumulator << 4) | GetHexValue(ch);
                        }

                        // if we successfully found the closing brace, return what we accumulated
                        // and advance past the }
                        if (ch == '}')
                        {
                            value = accumulator;
                            ++index;
                        }
                    }
                    else if (index + 5 < text.Length
                        && IsHexDigit(text[index + 2])
                        && IsHexDigit(text[index + 3])
                        && IsHexDigit(text[index + 4])
                        && IsHexDigit(text[index + 5]))
                    {
                        // must be \uXXXX
                        value = GetHexValue(text[index + 2]) << 12
                            | GetHexValue(text[index + 3]) << 8
                            | GetHexValue(text[index + 4]) << 4
                            | GetHexValue(text[index + 5]);
                        index += 6;
                    }
                    else
                    {
                        // error; skip over the \
                        ++index;
                    }
                }
            }

            return value;
        }

        static string PeekUnicodeEscape(string text, ref int index)
        {
            // decode the first escape sequence
            var codePoint = DecodeOneUnicodeEscapeSequence(text, ref index);

            // if this is a high-surrogate, try decoding a second escape sequence
            if (0xd800 <= codePoint && codePoint <= 0xdbff)
            {
                var savedIndex = index;
                var lowSurrogate = DecodeOneUnicodeEscapeSequence(text, ref index);
                if (0xdc00 <= lowSurrogate && lowSurrogate <= 0xdfff)
                {
                    return new string(new[] { (char)codePoint, (char)lowSurrogate });
                }
                else
                {
                    // a high-surrogate NOT followed by a low-surrogate is invalid, 
                    // but return it anyway after restoring our previous location
                    index = savedIndex;
                    return new string(new[] { (char)codePoint });
                }
            }
            else if (0xdc00 <= codePoint && codePoint <= 0xdfff)
            {
                // can't be a low-surrogate by itself, but return it anyway
                return new string(new[] { (char)codePoint });
            }
            else if (codePoint < 0 || codePoint > 0x10ffff)
            {
                // codepoints less than zero or higher than U+10FFFF are invalid
                return null;
            }

            // convert to a "character," which may be a surrogate pair.
            return char.ConvertFromUtf32(codePoint);
        }

        #endregion

        #region private Is methods

        static bool IsHexDigit(char c)
        {
            return ('0' <= c && c <= '9') || ('A' <= c && c <= 'F') || ('a' <= c && c <= 'f');
        }

        bool IsLineTerminator(char c, int increment)
        {
            switch (c)
            {
                case '\u000d':
                    // treat 0x0D0x0A as a single character
                    if (0x0A == GetChar(currentPosition + increment))
                    {
                        currentPosition++;
                    }

                    return true;

                case '\u000a':
                    return true;

                case '\u2028':
                    return true;

                case '\u2029':
                    return true;

                default:
                    return false;
            }
        }

        bool IsEndLineOrEOF(char c, int increment)
        {
            return IsLineTerminator(c, increment) || '\0' == c && IsEndOfFile;
        }

        static bool IsBlankSpace(char c)
        {
            switch (c)
            {
                case '\u0009':
                case '\u000b':
                case '\u000c':
                case '\u0020':
                case '\u00a0':
                case '\ufeff': // BOM - byte order mark
                    return true;

                default:
                    return (c >= 128) && CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;
            }
        }

        // This function return whether an operator is processable in ParseExpression.
        // Comma is out of this list and so are the unary ops
        internal static bool IsProcessableOperator(JSToken token)
        {
            return JSToken.FirstBinaryOperator <= token && token <= JSToken.ConditionalIf;
        }

        #endregion

        #region private preprocessor methods

        string PPScanIdentifier(bool forceUpper)
        {
            string identifier = null;

            // start at the current position
            var startPos = currentPosition;

            // see if the first character is a valid identifier start
            if (JSScanner.IsValidIdentifierStart(sourceCode, ref currentPosition))
            {
                // it is -- keep going as long as we have valid part characters
                while (JSScanner.IsValidIdentifierPart(sourceCode, ref currentPosition))
                {
                    // position is advanced in the condition
                }
            }

            // if we advanced at all, return the code we scanned. Otherwise return null
            if (currentPosition > startPos)
            {
                identifier = sourceCode.Substring(startPos, currentPosition - startPos);
                if (forceUpper)
                    identifier = identifier.ToUpperInvariant();
            }

            return identifier;
        }

        bool PPScanInteger(out int intValue)
        {
            var startPos = currentPosition;
            while (IsDigit(GetChar(currentPosition)))
                ++currentPosition;

            var success = false;
            if (currentPosition > startPos)
            {
                success = int.TryParse(sourceCode.Substring(startPos, currentPosition - startPos), out intValue);
            }
            else
            {
                intValue = 0;
            }

            return success;
        }

        int PPSkipToDirective(params string[] endStrings)
        {
            // save the current position - if we hit an EOF before we find a directive
            // we're looking for, we'll use this as the end of the error context so we
            // don't have the whole rest of the file printed out with the error.
            var endPosition = currentPosition;
            var endLineNum = CurrentLine;
            var endLinePos = StartLinePosition;

            while (true)
            {
                char c = GetChar(currentPosition++);
                switch (c)
                {
                    // EOF
                    case '\0':
                        if (IsEndOfFile)
                        {
                            // adjust the scanner state
                            currentPosition--;
                            CurrentToken.EndPosition = currentPosition;
                            CurrentToken.EndLineNumber = CurrentLine;
                            CurrentToken.EndLinePosition = StartLinePosition;

                            // create a clone of the current token and set the ending to be the end of the
                            // directive for which we're trying to find an end. Use THAT context for the 
                            // error context. Then throw an exception so we can bail.
                            var contextError = CurrentToken.Clone();
                            contextError.EndPosition = endPosition;
                            contextError.EndLineNumber = endLineNum;
                            contextError.EndLinePosition = endLinePos;
                            contextError.HandleError(string.CompareOrdinal(endStrings[0], "#ENDDEBUG") == 0
                                ? JSError.NoEndDebugDirective
                                : JSError.NoEndIfDirective);
                            throw new EndOfStreamException();
                        }

                        break;

                    // line terminator crap
                    case '\r':
                        if (GetChar(currentPosition) == '\n')
                        {
                            CurrentLine++;
                            currentPosition++;
                        }
                        StartLinePosition = currentPosition;
                        break;
                    case '\n':
                        CurrentLine++;
                        StartLinePosition = currentPosition;
                        break;
                    case '\u2028':
                        CurrentLine++;
                        StartLinePosition = currentPosition;
                        break;
                    case '\u2029':
                        CurrentLine++;
                        StartLinePosition = currentPosition;
                        break;

                    // check for /// (and then followed by any one of the substrings passed to us)
                    case '/':
                        if (CheckSubstring(currentPosition, "//"))
                        {
                            // skip it
                            currentPosition += 2;

                            // check to see if this is the start of ANOTHER preprocessor construct. If it
                            // is, then it's a NESTED statement and we'll need to recursively skip the 
                            // whole thing so everything stays on track
                            if (CheckCaseInsensitiveSubstring("#IFDEF")
                                || CheckCaseInsensitiveSubstring("#IFNDEF")
                                || CheckCaseInsensitiveSubstring("#IF"))
                            {
                                PPSkipToDirective("#ENDIF");
                            }
                            else
                            {
                                // now check each of the ending strings that were passed to us to see if one of
                                // them is a match
                                for (var ndx = 0; ndx < endStrings.Length; ++ndx)
                                {
                                    if (CheckCaseInsensitiveSubstring(endStrings[ndx]))
                                    {
                                        // found the ending string
                                        return ndx;
                                    }
                                }

                                // not something we're looking for -- but is it a simple ///#END?
                                if (CheckCaseInsensitiveSubstring("#END"))
                                {
                                    // if the current character is not whitespace, then it's not a simple "#END"
                                    c = GetChar(currentPosition);
                                    if (IsBlankSpace(c) || IsAtEndOfLine)
                                    {
                                        // it is! Well, we were expecting either #ENDIF or #ENDDEBUG, but we found just an #END.
                                        // that's not how the syntax is SUPPOSED to go. But let's let it fly.
                                        // the ending token is always the first one.
                                        return 0;
                                    }
                                }
                            }
                        }

                        break;
                }
            }
        }

        // returns true we should skip the rest of this line,
        // or false if we are already processed the whole thing.
        bool ScanPreprocessingDirective()
        {
            // check for some NUglify preprocessor comments
            if (CheckCaseInsensitiveSubstring("#GLOBALS"))
            {
                return ScanGlobalsDirective();
            }
            else if (CheckCaseInsensitiveSubstring("#SOURCE"))
            {
                return ScanSourceDirective();
            }
            else if (UsePreprocessorDefines)
            {
                if (CheckCaseInsensitiveSubstring("#DEBUG"))
                {
                    return ScanDebugDirective();
                }
                else if (CheckCaseInsensitiveSubstring("#IF"))
                {
                    return ScanIfDirective();
                }
                else if (CheckCaseInsensitiveSubstring("#ELSE") && ifDirectiveLevel > 0)
                {
                    return ScanElseDirective();
                }
                else if (CheckCaseInsensitiveSubstring("#ENDIF") && ifDirectiveLevel > 0)
                {
                    return ScanEndIfDirective();
                }
                else if (CheckCaseInsensitiveSubstring("#DEFINE"))
                {
                    return ScanDefineDirective();
                }
                else if (CheckCaseInsensitiveSubstring("#UNDEF"))
                {
                    return ScanUndefineDirective();
                }
            }

            return true;
        }

        bool ScanGlobalsDirective()
        {
            // found ///#GLOBALS comment
            SkipBlanks();

            // should be one or more space-separated identifiers
            while (!IsAtEndOfLine)
            {
                var identifier = PPScanIdentifier(false);
                if (identifier != null)
                {
                    OnGlobalDefine(identifier);
                }

                SkipBlanks();
            }

            return true;
        }

        bool ScanSourceDirective()
        {
            // found ///#SOURCE or /*/#SOURCE */ comment
            SkipBlanks();

            // pull the line, the column, and the source path off the line
            var linePos = 0;
            var colPos = 0;

            // line number is first
            if (PPScanInteger(out linePos))
            {
                SkipBlanks();

                // column number is second
                if (PPScanInteger(out colPos))
                {
                    SkipBlanks();

                    // the path should be the last part of the line.
                    // skip to the end and then use the part between.
                    var ndxStart = currentPosition;
                    SkipToEndOfLineOrComment();
                    if (currentPosition > ndxStart)
                    {
                        // there is a non-blank source token.
                        // so we have the line and the column and the source.
                        // use them. change the file context
                        var newModule = sourceCode.Substring(ndxStart, currentPosition - ndxStart).TrimEnd();

                        if (inMultipleLineComment)
                        {
                            // for a multi-line comment, we want to read the rest of the comment.
                            // this will leave us right after the */
                            SkipMultilineComment();
                        }

                        // for single-line comments, we stopped BEFORE the line terminator,
                        // so we still need to read ONE line terminator for the end of this line.
                        // for multi-line comments we skipped the terminating */, and we also want 
                        // to skip one line terminator, too, if there is one.
                        SkipOneLineTerminator();

                        // fire the event
                        CurrentToken.ChangeFileContext(newModule);

                        // adjust the line number
                        this.CurrentLine = linePos;

                        // the start line position is the current position less the column position.
                        // and because the column position in the comment is one-based, add one to get 
                        // back to zero-based: current - (col - 1)
                        this.StartLinePosition = currentPosition - colPos + 1;

                        // the source offset for both start and end is now the current position.
                        // this is assuming that a single token doesn't span across source files, which isn't
                        // entirely true, since a string literal or multi-line comment (for example) may do just that.
                        this.CurrentToken.SourceOffsetStart = this.CurrentToken.SourceOffsetEnd = currentPosition;

                        // alert anyone (the parser) that we encountered the start of a new module
                        OnNewModule(newModule);

                        // return false because we are all set on the next line and DON'T want the
                        // line skipped.
                        return false;
                    }
                }
            }

            // something isn't right; skip the rest of this line
            return true;
        }

        bool ScanIfDirective()
        {
            // we know we start with #IF -- see if it's #IFDEF or #IFNDEF
            var isIfDef = CheckCaseInsensitiveSubstring("DEF");
            var isIfNotDef = !isIfDef && CheckCaseInsensitiveSubstring("NDEF");

            // skip past the token and any blanks
            SkipBlanks();

            // if we encountered a line-break here, then ignore this directive
            if (!IsAtEndOfLine)
            {
                // get an identifier from the input
                var identifier = PPScanIdentifier(true);
                if (!string.IsNullOrEmpty(identifier))
                {
                    // set a state so that if we hit an #ELSE directive, we skip to #ENDIF
                    ++ifDirectiveLevel;

                    // if there is a dictionary AND the identifier is in it, then the identifier IS defined.
                    // if there is not dictionary OR the identifier is NOT in it, then it is NOT defined.
                    var isDefined = (defines != null && defines.ContainsKey(identifier));

                    // skip any blanks
                    SkipBlanks();

                    // if we are at the end of the line, or if this was an #IFDEF or #IFNDEF, then
                    // we have enough information to act
                    if (isIfDef || isIfNotDef || IsAtEndOfLine)
                    {
                        // either #IFDEF identifier, #IFNDEF identifier, or #IF identifier.
                        // this will simply test for whether or not it's defined
                        var conditionIsTrue = (!isIfNotDef && isDefined) || (isIfNotDef && !isDefined);

                        // if the condition is true, we just keep processing and when we hit the #END we're done,
                        // or if we hit an #ELSE we skip to the #END. But if we are not true, we need to skip to
                        // the #ELSE or #END directly.
                        if (!conditionIsTrue)
                        {
                            // the condition is FALSE!
                            // skip to #ELSE or #ENDIF and continue processing normally.
                            // (make sure the end if always the first one)
                            if (PPSkipToDirective("#ENDIF", "#ELSE") == 0)
                            {
                                // encountered the #ENDIF directive, so we know to reset the flag
                                --ifDirectiveLevel;
                            }
                        }
                    }
                    else
                    {
                        // this is an #IF and we have something after the identifier.
                        // it better be an operator or we'll ignore this comment.
                        var operation = CheckForOperator(PPOperators.Instance);
                        if (operation != null)
                        {
                            // skip any whitespace
                            SkipBlanks();

                            // save the current index -- this is either a non-whitespace character or the EOL.
                            // if it wasn't the EOL, skip to it now
                            var ndxStart = currentPosition;
                            if (!IsAtEndOfLine)
                            {
                                SkipToEndOfLineOrComment();
                            }

                            // the value to compare against is the substring between the start and the current.
                            // (and could be empty)
                            var compareTo = sourceCode.Substring(ndxStart, currentPosition - ndxStart);

                            // now do the comparison and see if it's true. If the identifier isn't even defined, then
                            // the condition is false.
                            var conditionIsTrue = isDefined && operation(defines[identifier], compareTo.TrimEnd());

                            // if the condition is true, we just keep processing and when we hit the #END we're done,
                            // or if we hit an #ELSE we skip to the #END. But if we are not true, we need to skip to
                            // the #ELSE or #END directly.
                            if (!conditionIsTrue)
                            {
                                // the condition is FALSE!
                                // skip to #ELSE or #ENDIF and continue processing normally.
                                // (make sure the end if always the first one)
                                if (PPSkipToDirective("#ENDIF", "#ELSE") == 0)
                                {
                                    // encountered the #ENDIF directive, so we know to reset the flag
                                    --ifDirectiveLevel;
                                }
                            }
                        }
                    }
                }
            }

            return true;
        }

        Func<string, string, bool> CheckForOperator(SortedDictionary<string, Func<string, string, bool>> operators)
        {
            // we need to make SURE we are checking the longer strings before we check the
            // shorter strings, because if the source is === and we check for ==, we'll pop positive
            // for it and miss that last =. 
            foreach (var entry in operators)
            {
                if (CheckCaseInsensitiveSubstring(entry.Key))
                {
                    // found it! return the comparison function for this text
                    return entry.Value;
                }
            }

            // if we got here, we didn't find anything we were looking for
            return null;
        }

        bool ScanElseDirective()
        {
            // reset the state that says we were in an #IFDEF construct
            --ifDirectiveLevel;

            // ...then we now want to skip until the #ENDIF directive
            PPSkipToDirective("#ENDIF");
            return true;
        }

        bool ScanEndIfDirective()
        {
            // reset the state that says we were in an #IFDEF construct
            --ifDirectiveLevel;
            return true;
        }

        bool ScanDefineDirective()
        {
            // skip past the token and any blanks
            SkipBlanks();

            // if we encountered a line-break here, then ignore this directive
            if (!IsAtEndOfLine)
            {
                // get an identifier from the input
                var identifier = PPScanIdentifier(true);
                if (!string.IsNullOrEmpty(identifier))
                {
                    // see if we're assigning a value
                    string value = string.Empty;
                    SkipBlanks();
                    if (GetChar(currentPosition) == '=')
                    {
                        // we are! get the rest of the line as the trimmed string
                        var ndxStart = ++currentPosition;
                        SkipToEndOfLineOrComment();
                        value = sourceCode.Substring(ndxStart, currentPosition - ndxStart).Trim();
                    }

                    // if there is no dictionary of defines yet, create one now
                    if (defines == null)
                    {
                        defines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    }

                    // if the identifier is not already in the dictionary, add it now
                    if (!defines.ContainsKey(identifier))
                    {
                        defines.Add(identifier, value);
                    }
                    else
                    {
                        // it already exists -- just set the value
                        defines[identifier] = value;
                    }
                }
            }

            return true;
        }

        bool ScanUndefineDirective()
        {
            // skip past the token and any blanks
            SkipBlanks();

            // if we encountered a line-break here, then ignore this directive
            if (!IsAtEndOfLine)
            {
                // get an identifier from the input
                var identifier = PPScanIdentifier(true);

                // if there was an identifier and we have a dictionary of "defines" and the
                // identifier is in that dictionary...
                if (!string.IsNullOrEmpty(identifier))
                {
                    if (defines != null && defines.ContainsKey(identifier))
                    {
                        // remove the identifier from the "defines" dictionary
                        defines.Remove(identifier);
                    }
                }
            }

            return true;
        }

        bool ScanDebugDirective()
        {
            // advance to the next character. If it's an equal sign, then this
            // debug comment is setting a debug namespace, not marking debug code.
            if (GetChar(currentPosition) == '=')
            {
                // we have ///#DEBUG=
                // get the namespace after the equal sign
                ++currentPosition;
                var identifier = PPScanIdentifier(false);
                if (identifier == null)
                {
                    // nothing. clear the debug namespaces
                    DebugLookupCollection.Clear();
                }
                else
                {
                    // this first identifier is the root namespace for the debug object.
                    // let's also treat it as a known global.
                    OnGlobalDefine(identifier);

                    // see if we have a period and keep looping to get IDENT(.IDENT)*
                    while (GetChar(currentPosition) == '.')
                    {
                        ++currentPosition;
                        var nextIdentifier = PPScanIdentifier(false);
                        if (nextIdentifier != null)
                        {
                            identifier += '.' + nextIdentifier;
                        }
                        else
                        {
                            // problem with the formatting -- ignore this comment
                            identifier = null;
                            break;
                        }
                    }

                    if (identifier != null)
                    {
                        // add the identifier to the debug list
                        DebugLookupCollection.Add(identifier);
                    }
                }
            }
            else if (StripDebugCommentBlocks && (defines == null || !defines.ContainsKey("DEBUG")))
            {
                // NOT a debug namespace assignment comment, so this is the start
                // of a debug block. If we are skipping debug blocks (the DEBUG name
                // is not defined AND StripDebugCommentBlocks is TRUE), then start skipping now.
                // skip until we hit ///#ENDDEBUG
                PPSkipToDirective("#ENDDEBUG");
            }

            return true;
        }

        #endregion

        #region private error methods

        void HandleError(JSError error)
        {
            CurrentToken.EndPosition = currentPosition;
            CurrentToken.EndLinePosition = StartLinePosition;
            CurrentToken.EndLineNumber = CurrentLine;

            if (!this.SuppressErrors)
            {
                CurrentToken.HandleError(error);
            }
        }

        JSToken IllegalCharacter()
        {
            HandleError(JSError.IllegalChar);
            return JSToken.Error;
        }

        #endregion

        /// <summary>
        /// Given an assignment operator (=, +=, -=, *=, /=, %=, &amp;=, |=, ^=, &lt;&lt;=, &gt;&gt;=, &gt;&gt;&gt;=), strip
        /// the assignment to return (+, -, *, /, %, &amp;, |, ^, &lt;&lt;, &gt;&gt;, &gt;&gt;&gt;). For all other operators,
        /// include the normal assign (=), just return the same operator token.
        /// This only works if the two groups of tokens are actually defined in those orders!!! 
        /// </summary>
        /// <param name="assignOp"></param>
        /// <returns></returns>
        public static JSToken StripAssignment(JSToken assignOp)
        {
            // gotta be an assignment operator
            if (IsAssignmentOperator(assignOp))
            {
                // get the delta from assign (=), which is the first assignment operator
                int delta = assignOp - JSToken.Assign;

                // assign (=) will be zero -- we don't want to modify that one, so if
                // the delta is GREATER than zero...
                if (delta > 0)
                {
                    // add it to the plus token, less one since the delta for +=
                    // will be one.
                    assignOp = JSToken.Plus + delta - 1;
                }
            }

            return assignOp;
        }

        public static OperatorPrecedence GetOperatorPrecedence(SourceContext op)
        {
            return op == null || op.Token == JSToken.None ? OperatorPrecedence.None : JSScanner.operatorsPrec[op.Token - JSToken.FirstBinaryOperator];
        }

        static bool[] InitializeValidIdentifierPartMap()
        {
            var validityMap = new bool[256];
            for (var ansiValue = 0; ansiValue < 256; ansiValue++)
            {
                validityMap[ansiValue] = IsValidIdentifierPart(new string((char)ansiValue, 1), 0, 1);
            }

            return validityMap;
        }

        static OperatorPrecedence[] InitOperatorsPrec()
        {
            OperatorPrecedence[] operatorsPrec = new OperatorPrecedence[JSToken.LastOperator - JSToken.FirstBinaryOperator + 1];

            operatorsPrec[JSToken.Plus - JSToken.FirstBinaryOperator] = OperatorPrecedence.Additive;
            operatorsPrec[JSToken.Minus - JSToken.FirstBinaryOperator] = OperatorPrecedence.Additive;

            operatorsPrec[JSToken.LogicalOr - JSToken.FirstBinaryOperator] = OperatorPrecedence.LogicalOr;
            operatorsPrec[JSToken.LogicalAnd - JSToken.FirstBinaryOperator] = OperatorPrecedence.LogicalAnd;
            operatorsPrec[JSToken.NullishCoalesce - JSToken.FirstBinaryOperator] = OperatorPrecedence.NullCoalesce;
            operatorsPrec[JSToken.BitwiseOr - JSToken.FirstBinaryOperator] = OperatorPrecedence.BitwiseOr;
            operatorsPrec[JSToken.BitwiseXor - JSToken.FirstBinaryOperator] = OperatorPrecedence.BitwiseXor;
            operatorsPrec[JSToken.BitwiseAnd - JSToken.FirstBinaryOperator] = OperatorPrecedence.BitwiseAnd;

            operatorsPrec[JSToken.Equal - JSToken.FirstBinaryOperator] = OperatorPrecedence.Equality;
            operatorsPrec[JSToken.NotEqual - JSToken.FirstBinaryOperator] = OperatorPrecedence.Equality;
            operatorsPrec[JSToken.StrictEqual - JSToken.FirstBinaryOperator] = OperatorPrecedence.Equality;
            operatorsPrec[JSToken.StrictNotEqual - JSToken.FirstBinaryOperator] = OperatorPrecedence.Equality;

            operatorsPrec[JSToken.InstanceOf - JSToken.FirstBinaryOperator] = OperatorPrecedence.Relational;
            operatorsPrec[JSToken.In - JSToken.FirstBinaryOperator] = OperatorPrecedence.Relational;
            operatorsPrec[JSToken.Of - JSToken.FirstBinaryOperator] = OperatorPrecedence.Relational;
            operatorsPrec[JSToken.GreaterThan - JSToken.FirstBinaryOperator] = OperatorPrecedence.Relational;
            operatorsPrec[JSToken.LessThan - JSToken.FirstBinaryOperator] = OperatorPrecedence.Relational;
            operatorsPrec[JSToken.LessThanEqual - JSToken.FirstBinaryOperator] = OperatorPrecedence.Relational;
            operatorsPrec[JSToken.GreaterThanEqual - JSToken.FirstBinaryOperator] = OperatorPrecedence.Relational;

            operatorsPrec[JSToken.LeftShift - JSToken.FirstBinaryOperator] = OperatorPrecedence.Shift;
            operatorsPrec[JSToken.RightShift - JSToken.FirstBinaryOperator] = OperatorPrecedence.Shift;
            operatorsPrec[JSToken.UnsignedRightShift - JSToken.FirstBinaryOperator] = OperatorPrecedence.Shift;

            operatorsPrec[JSToken.Multiply - JSToken.FirstBinaryOperator] = OperatorPrecedence.Multiplicative;
            operatorsPrec[JSToken.Divide - JSToken.FirstBinaryOperator] = OperatorPrecedence.Multiplicative;
            operatorsPrec[JSToken.Modulo - JSToken.FirstBinaryOperator] = OperatorPrecedence.Multiplicative;

            operatorsPrec[JSToken.Exponent - JSToken.FirstBinaryOperator] = OperatorPrecedence.Exponentiation;

            operatorsPrec[JSToken.Assign - JSToken.FirstBinaryOperator] = OperatorPrecedence.Assignment;
            operatorsPrec[JSToken.PlusAssign - JSToken.FirstBinaryOperator] = OperatorPrecedence.Assignment;
            operatorsPrec[JSToken.MinusAssign - JSToken.FirstBinaryOperator] = OperatorPrecedence.Assignment;
            operatorsPrec[JSToken.ExponentAssign - JSToken.FirstBinaryOperator] = OperatorPrecedence.Assignment;
            operatorsPrec[JSToken.MultiplyAssign - JSToken.FirstBinaryOperator] = OperatorPrecedence.Assignment;
            operatorsPrec[JSToken.DivideAssign - JSToken.FirstBinaryOperator] = OperatorPrecedence.Assignment;
            operatorsPrec[JSToken.BitwiseAndAssign - JSToken.FirstBinaryOperator] = OperatorPrecedence.Assignment;
            operatorsPrec[JSToken.BitwiseOrAssign - JSToken.FirstBinaryOperator] = OperatorPrecedence.Assignment;
            operatorsPrec[JSToken.BitwiseXorAssign - JSToken.FirstBinaryOperator] = OperatorPrecedence.Assignment;
            operatorsPrec[JSToken.ModuloAssign - JSToken.FirstBinaryOperator] = OperatorPrecedence.Assignment;
            operatorsPrec[JSToken.LeftShiftAssign - JSToken.FirstBinaryOperator] = OperatorPrecedence.Assignment;
            operatorsPrec[JSToken.RightShiftAssign - JSToken.FirstBinaryOperator] = OperatorPrecedence.Assignment;
            operatorsPrec[JSToken.UnsignedRightShiftAssign - JSToken.FirstBinaryOperator] = OperatorPrecedence.Assignment;
            operatorsPrec[JSToken.LogicalAndAssign - JSToken.FirstBinaryOperator] = OperatorPrecedence.Assignment;
            operatorsPrec[JSToken.LogicalNullishAssign - JSToken.FirstBinaryOperator] = OperatorPrecedence.Assignment;
            operatorsPrec[JSToken.LogicalOrAssign - JSToken.FirstBinaryOperator] = OperatorPrecedence.Assignment;

            operatorsPrec[JSToken.ConditionalIf - JSToken.FirstBinaryOperator] = OperatorPrecedence.Conditional;
            operatorsPrec[JSToken.Colon - JSToken.FirstBinaryOperator] = OperatorPrecedence.Conditional;

            operatorsPrec[JSToken.Comma - JSToken.FirstBinaryOperator] = OperatorPrecedence.Comma;

            return operatorsPrec;
        }

        /// <summary>
        /// Class for associating an operator with a function for ///#IF directives
        /// in a lazy-loaded manner. Doesn't create and initialize the dictionary
        /// until the scanner actually encounters syntax that needs it.
        /// The keys are sorted by length, decreasing (longest operators first).
        /// </summary>
        sealed class PPOperators : SortedDictionary<string, Func<string, string, bool>>
        {
            PPOperators()
                : base(new LengthComparer())
            {
                // add the operator information
                this.Add("==", PPIsEqual);
                this.Add("!=", PPIsNotEqual);
                this.Add("===", PPIsStrictEqual);
                this.Add("!==", PPIsNotStrictEqual);
                this.Add("<", PPIsLessThan);
                this.Add(">", PPIsGreaterThan);
                this.Add("<=", PPIsLessThanOrEqual);
                this.Add(">=", PPIsGreaterThanOrEqual);
            }

            #region thread-safe lazy-loading property and nested class

            public static PPOperators Instance
            {
                get
                {
                    return Nested.Instance;
                }
            }

            static class Nested
            {
                internal static readonly PPOperators Instance = new PPOperators();
            }

            #endregion

            #region sorting class

            /// <summary>
            /// Sorting class for the sorted dictionary base to make sure the operators are
            /// enumerated with the LONGEST strings first, before the shorter strings.
            /// </summary>
            class LengthComparer : IComparer<string>
            {
                public int Compare(string x, string y)
                {
                    var delta = x != null && y != null ? y.Length - x.Length : 0;
                    return delta != 0 ? delta : string.CompareOrdinal(x, y);
                }
            }

            #endregion

            #region condition execution methods

            static bool PPIsStrictEqual(string left, string right)
            {
                // strict comparison is only a string compare -- no conversion to float if
                // the string comparison fails.
                return string.Compare(left, right, StringComparison.OrdinalIgnoreCase) == 0;
            }

            static bool PPIsNotStrictEqual(string left, string right)
            {
                // strict comparison is only a string compare -- no conversion to float if
                // the string comparison fails.
                return string.Compare(left, right, StringComparison.OrdinalIgnoreCase) != 0;
            }

            static bool PPIsEqual(string left, string right)
            {
                // first see if a string compare works
                var isTrue = string.Compare(left, right, StringComparison.OrdinalIgnoreCase) == 0;

                // if not, then try converting both sides to double and doing the comparison
                if (!isTrue)
                {
                    double leftNumeric, rightNumeric;
                    if (ConvertToNumeric(left, right, out leftNumeric, out rightNumeric))
                    {
                        // they both converted successfully
                        isTrue = leftNumeric == rightNumeric;
                    }
                }

                return isTrue;
            }

            static bool PPIsNotEqual(string left, string right)
            {
                // first see if a string compare works
                var isTrue = string.Compare(left, right, StringComparison.OrdinalIgnoreCase) != 0;

                // if they AREN'T equal, then try converting both sides to double and doing the comparison
                if (isTrue)
                {
                    double leftNumeric, rightNumeric;
                    if (ConvertToNumeric(left, right, out leftNumeric, out rightNumeric))
                    {
                        // they both converted successfully
                        isTrue = leftNumeric != rightNumeric;
                    }
                }

                return isTrue;
            }

            static bool PPIsLessThan(string left, string right)
            {
                // only numeric comparisons
                bool isTrue = false;
                double leftNumeric, rightNumeric;
                if (ConvertToNumeric(left, right, out leftNumeric, out rightNumeric))
                {
                    // they both converted successfully
                    isTrue = leftNumeric < rightNumeric;
                }

                return isTrue;
            }

            static bool PPIsGreaterThan(string left, string right)
            {
                // only numeric comparisons
                bool isTrue = false;
                double leftNumeric, rightNumeric;
                if (ConvertToNumeric(left, right, out leftNumeric, out rightNumeric))
                {
                    // they both converted successfully
                    isTrue = leftNumeric > rightNumeric;
                }

                return isTrue;
            }

            static bool PPIsLessThanOrEqual(string left, string right)
            {
                // only numeric comparisons
                bool isTrue = false;
                double leftNumeric, rightNumeric;
                if (ConvertToNumeric(left, right, out leftNumeric, out rightNumeric))
                {
                    // they both converted successfully
                    isTrue = leftNumeric <= rightNumeric;
                }

                return isTrue;
            }

            static bool PPIsGreaterThanOrEqual(string left, string right)
            {
                // only numeric comparisons
                bool isTrue = false;
                double leftNumeric, rightNumeric;
                if (ConvertToNumeric(left, right, out leftNumeric, out rightNumeric))
                {
                    // they both converted successfully
                    isTrue = leftNumeric >= rightNumeric;
                }

                return isTrue;
            }

            #endregion

            #region static helper methods

            /// <summary>
            /// Try converting the two strings to doubles
            /// </summary>
            /// <param name="left">first string</param>
            /// <param name="right">second string</param>
            /// <param name="leftNumeric">first string converted to double</param>
            /// <param name="rightNumeric">second string converted to double</param>
            /// <returns>true if the conversion was successful; false otherwise</returns>
            static bool ConvertToNumeric(string left, string right, out double leftNumeric, out double rightNumeric)
            {
                rightNumeric = default(double);
                return double.TryParse(left, NumberStyles.Any, CultureInfo.InvariantCulture, out leftNumeric)
                    && double.TryParse(right, NumberStyles.Any, CultureInfo.InvariantCulture, out rightNumeric);
            }

            #endregion
        }
    }
}
