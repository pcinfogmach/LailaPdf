Imports System.ComponentModel
Imports System.IO
Imports System.Runtime.CompilerServices
Imports System.Text
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Markup
Imports System.Windows.Media
Imports System.Windows.Media.Imaging
Imports PDFiumSharp
Imports PDFiumSharp.Types

Public Class Viewer
    Implements INotifyPropertyChanged
    Implements IDisposable

    Public Property DoResetOnNewDocument As Boolean = True

    Private _skip As Boolean
    Private _zoomCursor As Cursor
    Private _p As List(Of PageData) = Nothing
    Private disposedValue As Boolean
    Private _isSelecting As Boolean = False
    Private _selection As TextRange = New TextRange()
    Private _doc As PdfDocument = Nothing
    Private _isCaptured As Boolean = False
    Private _fullText As String = Nothing
    Private _pageLength As List(Of Integer) = Nothing
    Private _charPos As Dictionary(Of Integer, Tuple(Of Integer, Integer)) = Nothing
    Private _matches As List(Of TextRange) = Nothing
    Private _selectedRects As List(Of (pageIndex As Integer, rect As FS_RECTF)) = New List(Of (pageIndex As Integer, rect As FS_RECTF))
    Private _lastMatchIndex As Integer = -1
    Private _lastDocument As Byte() = Nothing
    Private _docLock As Object = New Object()
    Private _searchCancellationTokenSource As CancellationTokenSource = Nothing
    Private _blocks As Dictionary(Of Integer, List(Of Tuple(Of Integer, Integer, Integer, Integer))) = Nothing
    Private _mouseDownPoint As Point = New Point()

    Public Sub New()
        ' This call is required by the designer.
        InitializeComponent()

        ' Add any initialization after the InitializeComponent() call.
        Dim cursorStream As Stream = New MemoryStream(My.Resources.zoom_in)
        _zoomCursor = New Cursor(cursorStream)

        ' enable anti-aliasing
        RenderOptions.SetBitmapScalingMode(Me, BitmapScalingMode.HighQuality)

        AddHandler Me.Loaded, AddressOf PdfiumViewer_Loaded

        Me.Focusable = True
    End Sub

    Private Sub PdfiumViewer_Loaded(sender As Object, e As EventArgs)
        Me.Focusable = True
    End Sub

    Public Shared ReadOnly ToolProperty As DependencyProperty =
        DependencyProperty.Register("Tool", GetType(Tool), GetType(Viewer),
            New FrameworkPropertyMetadata(Tool.Zoom, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, AddressOf OnToolChanged))

    Public Shared ReadOnly IsLoadingProperty As DependencyProperty =
        DependencyProperty.Register("IsLoading", GetType(Boolean), GetType(Viewer),
            New FrameworkPropertyMetadata(False, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault))

    Public Shared ReadOnly SearchTermProperty As DependencyProperty =
        DependencyProperty.Register("SearchTerm", GetType(String), GetType(Viewer),
            New FrameworkPropertyMetadata(Nothing, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, AddressOf OnSearchTermChanged))

    Public Shared ReadOnly NumberOfMatchesProperty As DependencyProperty =
        DependencyProperty.Register("NumberOfMatches", GetType(Integer?), GetType(Viewer),
            New FrameworkPropertyMetadata(Nothing, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault))

    Public Shared ReadOnly CurrentMatchIndexProperty As DependencyProperty =
        DependencyProperty.Register("CurrentMatchIndex", GetType(Integer?), GetType(Viewer),
            New FrameworkPropertyMetadata(Nothing, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, AddressOf OnCurrentMatchIndexChanged))

    Public Property Tool As Tool
        Get
            Return GetValue(ToolProperty)
        End Get
        Set(value As Tool)
            SetValue(ToolProperty, value)
        End Set
    End Property

    Public Property IsLoading As Boolean
        Get
            Return GetValue(IsLoadingProperty)
        End Get
        Set(value As Boolean)
            SetValue(IsLoadingProperty, value)
        End Set
    End Property

    Public Property SearchTerm As String
        Get
            Return GetValue(SearchTermProperty)
        End Get
        Set(value As String)
            SetValue(SearchTermProperty, value)
        End Set
    End Property

    Public Property NumberOfMatches As Integer?
        Get
            Return GetValue(NumberOfMatchesProperty)
        End Get
        Set(value As Integer?)
            SetValue(NumberOfMatchesProperty, value)
        End Set
    End Property

    Public Property CurrentMatchIndex As Integer?
        Get
            Return GetValue(CurrentMatchIndexProperty)
        End Get
        Set(value As Integer?)
            SetValue(CurrentMatchIndexProperty, value)
        End Set
    End Property

    Private Overloads Shared Sub OnToolChanged(ByVal d As DependencyObject, ByVal e As DependencyPropertyChangedEventArgs)
        CType(d, Viewer).OnToolChanged(e.OldValue)
    End Sub

    Private Overloads Shared Sub OnSearchTermChanged(ByVal d As DependencyObject, ByVal e As DependencyPropertyChangedEventArgs)
        CType(d, Viewer).OnSearchTermChanged()
    End Sub

    Private Overloads Shared Sub OnCurrentMatchIndexChanged(ByVal d As DependencyObject, ByVal e As DependencyPropertyChangedEventArgs)
        CType(d, Viewer).OnCurrentMatchIndexChanged()
    End Sub

    Public Overloads Sub OnToolChanged(oldValue As Tool)
        If oldValue = Tool.Form Then
            ' take focus away from fields
            _doc.Pages(0).Form.OnFocus(0, 0, 0)

            Debug.WriteLine("invalidate ln 147")
            Me.InvalidateVisual()
        End If
    End Sub

    Public Overloads Sub OnSearchTermChanged()
        Me.IsLoading = True

        If Not _searchCancellationTokenSource Is Nothing Then
            _searchCancellationTokenSource.Cancel()
        End If

        _searchCancellationTokenSource = New CancellationTokenSource()
        search(Me.SearchTerm, _searchCancellationTokenSource.Token)
    End Sub

    Private Async Sub search(searchTerm As String, cancellationToken As CancellationToken)
        If _doc Is Nothing Then
            Return
        End If

        Dim result As List(Of TextRange) = Await Task.Run(
            Async Function() As Task(Of List(Of TextRange))
                SyncLock _docLock
                    If Not _matches Is Nothing Then
                        For Each m In _matches
                            invalidateRangePages(m)
                            If cancellationToken.IsCancellationRequested Then Return Nothing
                        Next
                    End If

                    Dim matches As List(Of TextRange) = New List(Of TextRange)()

                    If searchTerm.Trim().Length > 0 Then
                        Dim filteredTerm As String = searchTerm.ToLower().Replace(Chr(32), "").Replace(Chr(13), "").Replace(Chr(10), "")
                        Dim i As Integer = _fullText.ToLower().IndexOf(filteredTerm)
                        While i >= 0
                            ' get page and index on which this match starts
                            Dim startPageIndex As Integer = _charPos(i).Item2
                            Dim startTextIndex As Integer = _charPos(i).Item1

                            ' get page and index on which this match ends
                            Dim endPageIndex As Integer = _charPos(i + filteredTerm.Length - 1).Item2
                            Dim endTextIndex As Integer = _charPos(i + filteredTerm.Length - 1).Item1 + 1

                            ' add match
                            Dim range As TextRange = New TextRange() With {
                                .StartPageIndex = startPageIndex,
                                .StartTextIndex = startTextIndex,
                                .EndPageIndex = endPageIndex,
                                .EndTextIndex = endTextIndex
                            }
                            matches.Add(range)
                            invalidateRangePages(range)

                            i = _fullText.ToLower().IndexOf(filteredTerm, i + 1)

                            If cancellationToken.IsCancellationRequested Then Return Nothing
                        End While
                    End If

                    Return matches
                End SyncLock
            End Function)

        If Not result Is Nothing Then
            _matches = result

            ' count matches
            If Not _matches Is Nothing Then
                Me.NumberOfMatches = _matches.Count
            Else
                Me.NumberOfMatches = 0
            End If
            Me.CurrentMatchIndex = 0
            Me.OnCurrentMatchIndexChanged()

            Me.IsLoading = False
        End If
    End Sub

    Public Overloads Sub OnCurrentMatchIndexChanged()
        If Not _matches Is Nothing AndAlso Me.CurrentMatchIndex >= 0 AndAlso Me.CurrentMatchIndex < _matches.Count Then
            Dim scrollbarHHeight As Integer = If(scrollBarH.Height = Double.NaN OrElse scrollBarH.Visibility <> Visibility.Visible, 0, scrollBarH.Height)
            Dim scrollbarVWidth As Integer = If(scrollBarV.Width = Double.NaN OrElse scrollBarV.Visibility <> Visibility.Visible, 0, scrollBarV.Width)

            ' get match
            Dim range As TextRange = _matches(Me.CurrentMatchIndex)
            invalidateRangePages(_selection)
            _selection.StartPageIndex = range.StartPageIndex
            _selection.StartTextIndex = range.StartTextIndex
            _selection.EndPageIndex = range.EndPageIndex
            _selection.EndTextIndex = range.EndTextIndex
            Me.IsTextSelected = _selection.StartPageIndex <> _selection.EndPageIndex OrElse _selection.StartTextIndex <> _selection.EndTextIndex

            ' get first and last rects
            Dim firstRect As FS_RECTF
            Dim lastRect As FS_RECTF
            Dim numRects As Integer
            If range.StartPageIndex = range.EndPageIndex Then
                numRects = _doc.Pages(range.StartPageIndex).TextPage.CountRects(range.StartTextIndex, range.EndTextIndex - range.StartTextIndex)
                firstRect = _doc.Pages(range.StartPageIndex).TextPage.GetRect(0)
                lastRect = _doc.Pages(range.StartPageIndex).TextPage.GetRect(numRects - 1)
            Else
                numRects = _doc.Pages(range.StartPageIndex).TextPage.CountRects(range.StartTextIndex, -1)
                firstRect = _doc.Pages(range.StartPageIndex).TextPage.GetRect(0)
                numRects = _doc.Pages(range.EndPageIndex).TextPage.CountRects(0, range.EndTextIndex)
                lastRect = _doc.Pages(range.EndPageIndex).TextPage.GetRect(numRects - 1)
            End If

            Dim lastp2 = _doc.Pages(range.EndPageIndex).PageToDevice((0, 0, _p(range.EndPageIndex).OriginalWidth, _p(range.EndPageIndex).OriginalHeight), lastRect.Right, lastRect.Bottom)
            Dim lastP As Point = getAbsolutePagePosition(range.EndPageIndex)
            lastp2.Y = lastp2.Y / _p(range.EndPageIndex).OriginalHeight * _p(range.EndPageIndex).Height + lastP.Y
            lastp2.X = lastp2.X / _p(range.EndPageIndex).OriginalWidth * _p(range.EndPageIndex).Width + lastP.X

            If scrollBarV.Value < lastp2.Y + 10 - (Me.ActualHeight - scrollbarHHeight) Then
                scrollBarV.Value = lastp2.Y + 10 - (Me.ActualHeight - scrollbarHHeight)
            End If
            If scrollBarV.Value > lastp2.Y - 10 Then
                scrollBarV.Value = lastp2.Y - 10
            End If

            If scrollBarH.Value < lastp2.X + 10 - (Me.ActualWidth - scrollbarVWidth) Then
                scrollBarH.Value = lastp2.X + 10 - (Me.ActualWidth - scrollbarVWidth)
            End If
            If scrollBarH.Value > lastp2.X - 10 Then
                scrollBarH.Value = lastp2.X - 10
            End If

            Dim firstp1 = _doc.Pages(range.StartPageIndex).PageToDevice((0, 0, _p(range.StartPageIndex).OriginalWidth, _p(range.StartPageIndex).OriginalHeight), firstRect.Left, firstRect.Top)
            Dim firstP As Point = getAbsolutePagePosition(range.StartPageIndex)
            firstp1.Y = firstp1.Y / _p(range.StartPageIndex).OriginalHeight * _p(range.StartPageIndex).Height + firstP.Y
            firstp1.X = firstp1.X / _p(range.StartPageIndex).OriginalWidth * _p(range.StartPageIndex).Width + firstP.X

            If scrollBarV.Value < firstp1.Y + 10 - (Me.ActualHeight - scrollbarHHeight) Then
                scrollBarV.Value = firstp1.Y + 10 - (Me.ActualHeight - scrollbarHHeight)
            End If
            If scrollBarV.Value > firstp1.Y - 10 Then
                scrollBarV.Value = firstp1.Y - 10
            End If

            If scrollBarH.Value < firstp1.X + 10 - (Me.ActualWidth - scrollbarVWidth) Then
                scrollBarH.Value = firstp1.X + 10 - (Me.ActualWidth - scrollbarVWidth)
            End If
            If scrollBarH.Value > firstp1.X - 10 Then
                scrollBarH.Value = firstp1.X - 10
            End If

            invalidateRangePages(range)
        ElseIf Me.CurrentMatchIndex = 0 AndAlso Me.NumberOfMatches = 0 Then
            invalidateRangePages(_selection)
            _selection = New TextRange()
        End If

        Debug.WriteLine("invalidate ln 322")
        Me.InvalidateVisual()
    End Sub

    Public Shared ReadOnly IsTextSelectedProperty As DependencyProperty =
        DependencyProperty.Register("IsTextSelected", GetType(Boolean), GetType(Viewer),
            New PropertyMetadata(Nothing))

    Public Property IsTextSelected As Boolean
        Get
            Return GetValue(IsTextSelectedProperty)
        End Get
        Set(value As Boolean)
            SetValue(IsTextSelectedProperty, value)
        End Set
    End Property

    Public ReadOnly Property SelectedText As String
        Get
            Dim normalizedSelection As TextRange = getNormalizedSelection()
            Dim text As String = ""
            Dim doSelectNextBlocks As Boolean = False

            For i = 0 To _p.Count - 1
                If i >= normalizedSelection.StartPageIndex AndAlso i <= normalizedSelection.EndPageIndex AndAlso
                            Not (normalizedSelection.StartPageIndex = normalizedSelection.EndPageIndex _
                                AndAlso normalizedSelection.StartTextIndex = normalizedSelection.EndTextIndex) Then
                    For Each block In _blocks(i)
                        If i = normalizedSelection.StartPageIndex AndAlso i = normalizedSelection.EndPageIndex Then
                            ' start selection page is end selection page
                            If normalizedSelection.StartTextIndex >= block.Item2 And normalizedSelection.StartTextIndex <= block.Item2 + block.Item3 _
                                AndAlso normalizedSelection.EndTextIndex >= block.Item2 And normalizedSelection.EndTextIndex <= block.Item2 + block.Item3 Then
                                ' start and end selection is in this block
                                text &= _doc.Pages(i).TextPage.GetText(normalizedSelection.StartTextIndex, normalizedSelection.EndTextIndex - normalizedSelection.StartTextIndex) & vbCrLf
                            ElseIf normalizedSelection.StartTextIndex >= block.Item2 And normalizedSelection.StartTextIndex <= block.Item2 + block.Item3 Then
                                ' start selection is in this block
                                text &= _doc.Pages(i).TextPage.GetText(normalizedSelection.StartTextIndex, block.Item2 + block.Item3 - normalizedSelection.StartTextIndex) & vbCrLf
                            ElseIf normalizedSelection.EndTextIndex >= block.Item2 And normalizedSelection.EndTextIndex <= block.Item2 + block.Item3 Then
                                ' end selection is in this block
                                text &= _doc.Pages(i).TextPage.GetText(block.Item2, normalizedSelection.EndTextIndex - block.Item2) & vbCrLf
                            Else
                                Dim blockIndex As Integer = _blocks(i).IndexOf(block)
                                If _blocks(i).Take(blockIndex).ToList().Exists(Function(b) normalizedSelection.StartTextIndex >= b.Item2 And normalizedSelection.StartTextIndex <= b.Item2 + b.Item3) _
                                    AndAlso _blocks(i).TakeLast(_blocks(i).Count - blockIndex - 1).ToList().Exists(Function(b) normalizedSelection.EndTextIndex >= b.Item2 And normalizedSelection.EndTextIndex <= b.Item2 + b.Item3) Then
                                    ' start is before this block and end is after this block
                                    text &= _doc.Pages(i).TextPage.GetText(block.Item2, block.Item3) & vbCrLf
                                End If
                            End If
                        ElseIf i = normalizedSelection.StartPageIndex Then
                            ' start selection is on this page
                            If doSelectNextBlocks Then
                                ' if we're past the start selection, select full block
                                text &= _doc.Pages(i).TextPage.GetText(block.Item2, block.Item3) & vbCrLf
                            ElseIf normalizedSelection.StartTextIndex >= block.Item2 And normalizedSelection.StartTextIndex <= block.Item2 + block.Item3 Then
                                ' selection starts in this block
                                text &= _doc.Pages(i).TextPage.GetText(normalizedSelection.StartTextIndex, block.Item2 + block.Item3 - normalizedSelection.StartTextIndex) & vbCrLf
                                doSelectNextBlocks = True
                            End If
                        ElseIf i = normalizedSelection.EndPageIndex Then
                            If normalizedSelection.EndTextIndex >= block.Item2 And normalizedSelection.EndTextIndex <= block.Item2 + block.Item3 Then
                                ' selection ends in this block
                                text &= _doc.Pages(i).TextPage.GetText(block.Item2, normalizedSelection.EndTextIndex - block.Item2) & vbCrLf
                                doSelectNextBlocks = False
                            ElseIf doSelectNextBlocks Then
                                ' if we're not past the end of the selection, select full block
                                text &= _doc.Pages(i).TextPage.GetText(block.Item2, block.Item3) & vbCrLf
                            End If
                        Else
                            ' full page is selected
                            text &= _doc.Pages(i).TextPage.GetText(block.Item2, block.Item3) & vbCrLf
                        End If
                    Next
                End If
            Next

            Return text
        End Get
    End Property

    Protected Overrides Sub OnKeyDown(e As KeyEventArgs)
        setCursor()
        If Keyboard.Modifiers.HasFlag(ModifierKeys.Control) AndAlso e.Key = Key.C Then
            Me.CopyTextToClipboard()
        ElseIf Keyboard.Modifiers.HasFlag(ModifierKeys.Control) AndAlso e.Key = Key.V AndAlso Me.Tool = Tool.Form Then
            Dim pageIndex As Integer = getCenteredPageIndex()
            _doc.Pages(pageIndex).Form.ReplaceSelection(Clipboard.GetText())
        ElseIf Keyboard.Modifiers.HasFlag(ModifierKeys.Control) AndAlso e.Key = Key.A Then
            Select Case Me.Tool
                Case Tool.Zoom, Tool.Select
                    _selection.StartPageIndex = 0
                    _selection.EndPageIndex = _doc.Pages.Count - 1
                    _selection.StartTextIndex = 0
                    _selection.EndTextIndex = _doc.Pages(_selection.EndPageIndex).TextPage.CountChars

                    Me.IsTextSelected = _selection.StartPageIndex <> _selection.EndPageIndex OrElse _selection.StartTextIndex <> _selection.EndTextIndex

                    Debug.WriteLine("invalidate ln 388")
                    Me.InvalidateVisual()
                Case Tool.Form
                    Dim pageIndex As Integer = getCenteredPageIndex()
                    _doc.Pages(pageIndex).Form.SelectAllText()
            End Select
        Else
            If Not _doc Is Nothing AndAlso Not _p Is Nothing Then
                Debug.WriteLine("keydown " & e.Key.ToString())
                Dim k As Integer = KeyInterop.VirtualKeyFromKey(e.Key)

                Dim modifier As KeyboardModifier = KeyboardModifier.None
                If Keyboard.Modifiers.HasFlag(ModifierKeys.Control) Then
                    modifier = modifier Or KeyboardModifier.ControlKey
                End If
                If Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) Then
                    modifier = modifier Or KeyboardModifier.ShiftKey
                End If
                If Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) Then
                    modifier = modifier Or KeyboardModifier.AltKey
                End If
                Debug.WriteLine(k & "   " & modifier)

                Dim pageIndex As Integer = getCenteredPageIndex()
                If _doc.Pages(pageIndex).Form.OnKeyDown(modifier, k) Then
                    If e.Key <> Key.Space Then e.Handled = True
                End If

                ' fix for backspace
                If k = 8 Then
                    If _doc.Pages(pageIndex).Form.OnChar(modifier, 8) Then
                        e.Handled = True
                    End If
                End If
            End If
        End If
    End Sub

    Protected Overrides Sub OnTextInput(e As TextCompositionEventArgs)
        MyBase.OnTextInput(e)

        Dim c As Integer = 0

        If e.Text.Length > 0 Then
            c = Convert.ToInt32(e.Text.ToCharArray()(0))
        End If

        If c > 0 Then
            If Not _doc Is Nothing AndAlso Not _p Is Nothing Then
                Dim modifier As KeyboardModifier = KeyboardModifier.None
                If Keyboard.Modifiers.HasFlag(ModifierKeys.Control) Then
                    modifier = modifier Or KeyboardModifier.ControlKey
                End If
                If Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) Then
                    modifier = modifier Or KeyboardModifier.ShiftKey
                End If
                If Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) Then
                    modifier = modifier Or KeyboardModifier.AltKey
                End If

                Dim pageIndex As Integer = getCenteredPageIndex()
                If _doc.Pages(pageIndex).Form.OnChar(modifier, c) Then
                    e.Handled = True
                End If
            End If
        End If
    End Sub

    Public Sub CopyTextToClipboard()
        If Me.Tool = Tool.Form Then
            Dim pageIndex As Integer = getCenteredPageIndex()
            Clipboard.SetText(_doc.Pages(pageIndex).Form.GetSelectedText())
        Else
            Clipboard.SetText(Me.SelectedText)
        End If
    End Sub

    Protected Overrides Sub OnKeyUp(e As KeyEventArgs)
        setCursor()
        _isSelecting = False

        If Not _doc Is Nothing AndAlso Not _p Is Nothing Then
            Dim k As Integer = KeyInterop.VirtualKeyFromKey(e.Key)

            Dim modifier As KeyboardModifier = KeyboardModifier.None
            If Keyboard.Modifiers.HasFlag(ModifierKeys.Control) Then
                modifier = modifier Or KeyboardModifier.ControlKey
            End If
            If Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) Then
                modifier = modifier Or KeyboardModifier.ShiftKey
            End If
            If Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) Then
                modifier = modifier Or KeyboardModifier.AltKey
            End If

            Dim pageIndex As Integer = getCenteredPageIndex()
            If _doc.Pages(pageIndex).Form.OnKeyUp(modifier, k) Then
                If e.Key <> Key.Space Then e.Handled = True
            End If
        End If
    End Sub

#Region "Mouse"
    Protected Overrides Sub OnMouseDown(e As MouseButtonEventArgs)
        Me.Focus()

        If e.OriginalSource.Equals(grid) Then
            Dim point As Point = e.GetPosition(Me)
            _mouseDownPoint = point
            Dim pageIndex As Integer = getPageByPoint(point)
            If pageIndex <> -1 Then
                Select Case Me.Tool
                    Case Tool.Zoom
                        If e.ChangedButton = MouseButton.Left Then
                            Me.ZoomTo(Me.Zoom + 10, point.X, point.Y)
                        ElseIf e.ChangedButton = MouseButton.Right Then
                            Me.ZoomTo(Me.Zoom - 10, point.X, point.Y)
                        End If
                    Case Tool.Select
                        invalidateRangePages(_selection)

                        Dim p4 = translatePointToPage(pageIndex, point)
                        ' translate point to page
                        Dim p3 = _doc.Pages(pageIndex).DeviceToPage((0, 0, _doc.Pages(pageIndex).Width, _doc.Pages(pageIndex).Height), p4.X, p4.Y)
                        Dim charIndex As Integer = _doc.Pages(pageIndex).TextPage.GetCharIndexAtPos(p3.X, p3.Y, 100, 100)
                        If charIndex <> -1 Then
                            _isSelecting = True
                            _selection.StartPageIndex = pageIndex
                            _selection.StartTextIndex = charIndex '_doc.Pages(pageIndex).TextPage.GetTextIndexFromCharIndex(charIndex)
                            _selection.EndPageIndex = _selection.StartPageIndex
                            _selection.EndTextIndex = _selection.StartTextIndex
                            Debug.WriteLine("starting selection @ " & charIndex & ", " & pageIndex)
                            Mouse.Capture(Me)
                            _isCaptured = True
                        Else
                            _selection.StartPageIndex = -1
                            _selection.EndPageIndex = -1
                        End If

                        Me.IsTextSelected = _selection.StartPageIndex <> _selection.EndPageIndex OrElse _selection.StartTextIndex <> _selection.EndTextIndex
                        invalidateRangePages(_selection)

                        Debug.WriteLine("invalidate ln 521")
                        Me.InvalidateVisual()
                    Case Tool.Form
                        If Not _doc.IsDisposed Then
                            Dim p4 = translatePointToPage(pageIndex, point)
                            Dim p3 = _doc.Pages(pageIndex).DeviceToPage((0, 0, _doc.Pages(pageIndex).Width, _doc.Pages(pageIndex).Height), p4.X, p4.Y)
                            If _doc.Pages(pageIndex).Form.HasFormFieldAtPoint(p3.X, p3.Y) = -1 Then
                                _doc.Pages(pageIndex).Form.OnFocus(0, p3.X, p3.Y)
                            End If
                            If e.ChangedButton = MouseButton.Left Then
                                _doc.Pages(pageIndex).Form.OnLButtonDown(0, p3.X, p3.Y)
                            ElseIf e.ChangedButton = MouseButton.Right Then
                                _doc.Pages(pageIndex).Form.OnRButtonDown(0, p3.X, p3.Y)
                            End If
                        End If
                End Select
            End If
        End If
    End Sub

    Protected Overrides Sub OnMouseUp(e As MouseButtonEventArgs)
        _isSelecting = False
        If _isCaptured Then Me.ReleaseMouseCapture()
        If Not _scrollTimer Is Nothing Then
            _scrollTimer.Change(Timeout.Infinite, Timeout.Infinite)
            _scrollTimer = Nothing
        End If

        Dim point As System.Windows.Point = e.GetPosition(Me)
        Dim pageIndex As Integer = getPageByPoint(point)
        If pageIndex <> -1 AndAlso e.ChangedButton = MouseButton.Left AndAlso Not _doc.IsDisposed Then
            Select Case Me.Tool
                Case Tool.Form
                    Dim p4 = translatePointToPage(pageIndex, point)
                    Dim p3 = _doc.Pages(pageIndex).DeviceToPage((0, 0, _doc.Pages(pageIndex).Width, _doc.Pages(pageIndex).Height), p4.X, p4.Y)
                    If e.ChangedButton = MouseButton.Left Then
                        _doc.Pages(pageIndex).Form.OnLButtonUp(0, p3.X, p3.Y)
                    ElseIf e.ChangedButton = MouseButton.Right Then
                        _doc.Pages(pageIndex).Form.OnRButtonUp(0, p3.X, p3.Y)
                    End If
            End Select
        End If
    End Sub

    Protected Overrides Sub OnMouseDoubleClick(e As MouseButtonEventArgs)
        If e.OriginalSource.Equals(grid) Then
            Dim point As System.Windows.Point = e.GetPosition(Me)
            Dim pageIndex As Integer = getPageByPoint(point)
            If pageIndex <> -1 Then
                Select Case Me.Tool
                    Case Tool.Select
                        invalidateRangePages(_selection)

                        Dim p4 = translatePointToPage(pageIndex, point)
                        ' translate point to page
                        Dim p3 = _doc.Pages(pageIndex).DeviceToPage((0, 0, _doc.Pages(pageIndex).Width, _doc.Pages(pageIndex).Height), p4.X, p4.Y)
                        Dim charIndex As Integer = _doc.Pages(pageIndex).TextPage.GetCharIndexAtPos(p3.X, p3.Y, 5, 5)
                        If charIndex <> -1 Then
                            Dim text As String = _doc.Pages(pageIndex).TextPage.GetText(0, _doc.Pages(pageIndex).TextPage.CountChars)
                            If text.Substring(charIndex, 1) <> " " AndAlso text.Substring(charIndex, 1) <> vbCr AndAlso text.Substring(charIndex, 1) <> vbLf Then
                                _selection.StartPageIndex = pageIndex
                                _selection.EndPageIndex = pageIndex
                                _selection.StartTextIndex = charIndex
                                Dim i As Integer
                                For i = charIndex To 0 Step -1
                                    If text.Substring(i, 1) = " " OrElse text.Substring(i, 1) = vbCr OrElse text.Substring(i, 1) = vbLf Then
                                        _selection.StartTextIndex = i + 1
                                        Exit For
                                    End If
                                Next
                                If i <= 0 Then _selection.StartTextIndex = 0
                                _selection.EndTextIndex = charIndex
                                For i = charIndex To _doc.Pages(pageIndex).TextPage.CountChars - 1
                                    If text.Substring(i, 1) = " " OrElse text.Substring(i, 1) = vbCr OrElse text.Substring(i, 1) = vbLf Then
                                        _selection.EndTextIndex = i
                                        Exit For
                                    End If
                                Next
                                If i >= _doc.Pages(pageIndex).TextPage.CountChars Then _selection.EndTextIndex = _doc.Pages(pageIndex).TextPage.CountChars
                            Else
                                _selection.StartPageIndex = -1
                                _selection.EndPageIndex = -1
                            End If
                        Else
                            _selection.StartPageIndex = -1
                            _selection.EndPageIndex = -1
                        End If

                        Me.IsTextSelected = _selection.StartPageIndex <> _selection.EndPageIndex OrElse _selection.StartTextIndex <> _selection.EndTextIndex
                        invalidateRangePages(_selection)

                        Debug.WriteLine("invalidate ln 613")
                        Me.InvalidateVisual()
                    Case Tool.Form
                        Dim p4 = translatePointToPage(pageIndex, point)
                        Dim p3 = _doc.Pages(pageIndex).DeviceToPage((0, 0, _doc.Pages(pageIndex).Width, _doc.Pages(pageIndex).Height), p4.X, p4.Y)
                        _doc.Pages(pageIndex).Form.OnLButtonDoubleClick(0, p3.X, p3.Y)
                End Select
            End If
        End If
    End Sub

    Private _scrollTimer As Timer = Nothing
    Private _scrollPoint As System.Windows.Point

    Protected Overrides Sub OnMouseMove(e As MouseEventArgs)
        Dim scrollbarVWidth As Integer = If(scrollBarV.Width = Double.NaN OrElse scrollBarV.Visibility <> Visibility.Visible, 0, scrollBarV.Width)
        Dim scrollbarHHeight As Integer = If(scrollBarH.Height = Double.NaN OrElse scrollBarH.Visibility <> Visibility.Visible, 0, scrollBarH.Height)

        Select Case Me.Tool
            Case Tool.Zoom
                setCursor()
            Case Tool.Select
                setCursor()

                If _isSelecting Then
                    Dim point As System.Windows.Point = e.GetPosition(Me)
                    If point.Y < 0 OrElse point.Y > Me.ActualHeight - scrollbarHHeight OrElse point.X < 0 OrElse point.X > Me.ActualWidth - scrollbarVWidth Then
                        _scrollPoint = point
                        If _scrollTimer Is Nothing Then
                            _scrollTimer = New Timer(
                                Sub()
                                    System.Windows.Application.Current.Dispatcher.Invoke(
                                        Sub()
                                            point = _scrollPoint
                                            If _scrollPoint.Y < 0 Then
                                                scrollBarV.Value -= Math.Abs(_scrollPoint.Y)
                                                Dim r As Rect = getPageRectangle(getFirstVisiblePageIndex())
                                                If r.Y > 0 Then
                                                    point.Y = r.Y + 1
                                                Else
                                                    point.Y = 1
                                                End If
                                                Debug.WriteLine("invalidate ln 655")
                                                InvalidateVisual()
                                            ElseIf _scrollPoint.Y > Me.ActualHeight - scrollbarHHeight Then
                                                scrollBarV.Value += Math.Abs(_scrollPoint.Y - Me.ActualHeight - scrollbarHHeight)
                                                Dim lastpageIndex As Integer = getFirstVisiblePageIndex() + getNumberOfVisiblePages()
                                                If lastpageIndex > _p.Count - 1 Then lastpageIndex = _p.Count - 1
                                                Debug.WriteLine("getFirstVisiblePageIndex()=" & getFirstVisiblePageIndex() & "  getNumberOfVisiblePages()=" & getNumberOfVisiblePages() & " lastpageIndex=" & lastpageIndex)
                                                Dim r As Rect = getPageRectangle(lastpageIndex)
                                                If r.Y + r.Height < Me.ActualHeight - scrollbarHHeight Then
                                                    point.Y = r.Y + r.Height - 1
                                                Else
                                                    point.Y = Me.ActualHeight - scrollbarHHeight - 1
                                                End If
                                                Debug.WriteLine("invalidate ln 668")
                                                InvalidateVisual()
                                            End If
                                            If _scrollPoint.X < 0 Then
                                                scrollBarH.Value -= Math.Abs(_scrollPoint.X)
                                                Dim pageNo As Integer = getPageByPoint(New System.Windows.Point() With {.Y = _scrollPoint.Y, .X = -1})
                                                If pageNo < 0 Then pageNo = 0
                                                If pageNo > _p.Count - 1 Then pageNo = _p.Count - 1
                                                Dim r As Rect = getPageRectangle(pageNo)
                                                If r.X > 0 Then
                                                    point.X = r.X + 1
                                                Else
                                                    point.X = 1
                                                End If
                                                Debug.WriteLine("invalidate ln 679")
                                                InvalidateVisual()
                                            ElseIf _scrollPoint.X > Me.ActualWidth - scrollbarVWidth Then
                                                scrollBarH.Value += Math.Abs(_scrollPoint.X - Me.ActualWidth - scrollbarVWidth)
                                                Dim pageNo As Integer = getPageByPoint(New System.Windows.Point() With {.Y = _scrollPoint.Y, .X = -1})
                                                If pageNo < 0 Then pageNo = 0
                                                If pageNo > _p.Count - 1 Then pageNo = _p.Count - 1
                                                Dim r As Rect = getPageRectangle(pageNo)
                                                If r.X + r.Width < Me.ActualWidth - scrollbarVWidth Then
                                                    point.X = r.X + r.Width - 1
                                                Else
                                                    point.X = Me.ActualWidth - scrollbarVWidth - 1
                                                End If
                                                Debug.WriteLine("invalidate ln 689")
                                                InvalidateVisual()
                                            End If

                                            mouseMoved(point)
                                        End Sub)
                                End Sub, Nothing, 0, 100)
                        End If
                    Else
                        If Not _scrollTimer Is Nothing Then
                            _scrollTimer.Change(Timeout.Infinite, Timeout.Infinite)
                            _scrollTimer = Nothing
                        End If
                        mouseMoved(point)
                    End If
                End If
            Case Tool.Form
                SyncLock _docLock
                    Dim point As System.Windows.Point = e.GetPosition(Me)
                    Dim pageIndex As Integer = getPageByPoint(point)
                    If pageIndex <> -1 AndAlso Not _doc.IsDisposed Then
                        Dim p4 = translatePointToPage(pageIndex, point)
                        Dim p3 = _doc.Pages(pageIndex).DeviceToPage((0, 0, _doc.Pages(pageIndex).Width, _doc.Pages(pageIndex).Height), p4.X, p4.Y)
                        If _doc.Pages(pageIndex).Form.HasFormFieldAtPoint(p3.X, p3.Y) = -1 Then
                            Me.Cursor = Nothing
                        End If
                        _doc.Pages(pageIndex).Form.OnMouseMove(0, p3.X, p3.Y)
                    End If
                End SyncLock
        End Select
    End Sub

    Private Sub mouseMoved(point As Point)
        Dim prevEndSelectionPage As Integer = _selection.EndPageIndex
        Dim prevEndSelectionTextPos As Integer = _selection.EndTextIndex

        Dim pageIndex As Integer = getPageByPoint(point)
        If pageIndex <> -1 Then
            Dim p4 = translatePointToPage(pageIndex, point)
            ' translate point to page
            Dim p3 = _doc.Pages(pageIndex).DeviceToPage((0, 0, _doc.Pages(pageIndex).Width, _doc.Pages(pageIndex).Height), p4.X, p4.Y)
            Dim charIndex As Integer = _doc.Pages(pageIndex).TextPage.GetCharIndexAtPos(p3.X, p3.Y, 10, 10)
            If charIndex <> -1 Then
                _selection.EndPageIndex = pageIndex
                _selection.EndTextIndex = charIndex
                If Not _selection.EndTextIndex = _selection.StartTextIndex AndAlso _selection.EndPageIndex = _selection.StartPageIndex Then
                    _selection.EndTextIndex += 1
                End If
                Debug.WriteLine("selection to @ " & charIndex & ", " & pageIndex)
            ElseIf Not (_mouseDownPoint.X = point.X AndAlso _mouseDownPoint.Y = point.Y) Then
                ' no character found, find last block above current mouse pos
                Dim lastBlock As Tuple(Of Integer, Integer, Integer, Integer) = _blocks(pageIndex).LastOrDefault(Function(b) b.Item4 < p4.Y)
                If Not lastBlock Is Nothing Then
                    _selection.EndPageIndex = pageIndex
                    _selection.EndTextIndex = lastBlock.Item2 + lastBlock.Item3
                End If
            End If

            If Not prevEndSelectionPage = _selection.EndPageIndex OrElse Not prevEndSelectionTextPos = _selection.EndTextIndex Then
                Me.IsTextSelected = _selection.StartPageIndex <> _selection.EndPageIndex OrElse _selection.StartTextIndex <> _selection.EndTextIndex

                invalidateRangePages(New TextRange() With {
                    .StartPageIndex = _selection.StartPageIndex,
                    .StartTextIndex = _selection.StartTextIndex,
                    .EndPageIndex = prevEndSelectionPage,
                    .EndTextIndex = prevEndSelectionTextPos
                })
                invalidateRangePages(_selection)
                Debug.WriteLine("invalidate ln 749")
                Me.InvalidateVisual()
            End If
        End If
    End Sub

    Private Sub invalidateRangePages(range As TextRange)
        SyncLock _docLock
            If range.StartPageIndex >= 0 AndAlso range.EndPageIndex >= 0 Then
                Dim startPageIndex As Integer, endPageIndex As Integer
                If range.EndPageIndex > range.StartPageIndex Then
                    startPageIndex = range.StartPageIndex
                    endPageIndex = range.EndPageIndex
                Else
                    startPageIndex = range.EndPageIndex
                    endPageIndex = range.StartPageIndex
                End If

                For i = startPageIndex To endPageIndex
                    _p(i).DoResetWritableBitmapPage = True
                Next
            End If
        End SyncLock
    End Sub
#End Region
#Region "Document Property"
    Public Shared ReadOnly DocumentProperty As DependencyProperty =
        DependencyProperty.Register("Document", GetType(Byte()), GetType(Viewer),
            New PropertyMetadata(Nothing, AddressOf OnDocumentChanged))

    Private Overloads Shared Sub OnDocumentChanged(ByVal d As DependencyObject, ByVal e As DependencyPropertyChangedEventArgs)
        CType(d, Viewer).OnDocumentChanged()
    End Sub

    Public Property Document As Byte()
        Get
            Return GetValue(DocumentProperty)
        End Get
        Set(value As Byte())
            SetValue(DocumentProperty, value)
        End Set
    End Property

    Protected Overridable Function GetExceptionText(ex As Exception) As String
        Return ex.Message
    End Function

    Public Overloads Sub OnDocumentChanged()
        Dim bytes As Byte() = GetValue(DocumentProperty)

        Me.IsLoading = True
        errorGrid.Visibility = Visibility.Collapsed

        SyncLock _docLock
            ' dispose previous doc
            If Not _doc Is Nothing Then
                CType(_doc, IDisposable).Dispose()
            End If

            ' open doc on dispatcher thread because this is the thread IDisposeable.Dispose works on
            _doc = New PdfDocument(bytes, 0, bytes.Length)
            For Each page In _doc.Pages
                Dim f As PdfForm = page.Form
                Dim tp As PdfTextPage = page.TextPage
            Next
        End SyncLock

        Dim t As Thread = New Thread(New ThreadStart(
            Sub()
                Dim appl As Application = Application.Current
                If appl Is Nothing Then
                    Return
                End If

                Dim isFirst As Boolean = _p Is Nothing

                SyncLock _docLock
                    _p = New List(Of PageData)()
                    _matches = Nothing
                    _selection = New TextRange()
                    _fullText = Nothing
                    _blocks = New Dictionary(Of Integer, List(Of Tuple(Of Integer, Integer, Integer, Integer)))

                    Try
                        ' get full text of PDF
                        Dim pageIndex As Integer = 0
                        Dim charIndexOffset As Integer = 0
                        Dim totalCharIndex As Integer = 0
                        _fullText = ""
                        _charPos = New Dictionary(Of Integer, Tuple(Of Integer, Integer))()
                        Dim separators As List(Of Char) = New List(Of Char) From {Chr(32), Chr(13), Chr(10), Chr(9)}
                        Dim blockSeparators As List(Of Char) = New List(Of Char) From {Chr(13), Chr(10)}
                        For Each page In _doc.Pages
                            ' get text for page
                            Dim text As String = ""
                            If page.TextPage.CountChars > 0 Then
                                text = page.TextPage.GetText(0, page.TextPage.CountChars)
                            End If

                            ' store indexes
                            Dim charIndex As Integer = 0
                            Dim pageBlocks As List(Of Tuple(Of Integer, Integer, Integer, Integer)) = New List(Of Tuple(Of Integer, Integer, Integer, Integer))
                            Dim isLastSeparator As Boolean = True
                            For Each c In text.ToCharArray()
                                If Not blockSeparators.Contains(c) AndAlso isLastSeparator Then
                                    Dim numRects As Integer = page.TextPage.CountRects(charIndex, 1)
                                    If numRects > 0 Then
                                        ' ...get coordinates
                                        Dim r2 As FS_RECTF = page.TextPage.GetRect(0)

                                        ' translate coordinates to device
                                        Dim p4 = page.PageToDevice((0, 0, page.Width, page.Height), r2.Left, r2.Top)
                                        Dim p5 = page.PageToDevice((0, 0, page.Width, page.Height), r2.Right, r2.Bottom)

                                        ' get block length
                                        Dim nextSeparatorIndex As Integer = Integer.MaxValue
                                        For Each cs In blockSeparators
                                            Dim checkIndex As Integer = text.IndexOf(cs, charIndex + 1)
                                            If checkIndex >= 0 AndAlso checkIndex < nextSeparatorIndex Then
                                                nextSeparatorIndex = checkIndex
                                            End If
                                        Next
                                        If nextSeparatorIndex = Integer.MaxValue Then
                                            nextSeparatorIndex = text.Length
                                        End If

                                        ' store block position, index and length
                                        pageBlocks.Add(New Tuple(Of Integer, Integer, Integer, Integer) _
                                            (p4.Y, charIndex, nextSeparatorIndex - charIndex, p5.Y))
                                    End If
                                End If

                                If Not separators.Contains(c) Then
                                    isLastSeparator = False
                                Else
                                    isLastSeparator = blockSeparators.Contains(c)
                                End If
                                charIndex += 1
                            Next

                            _blocks.Add(pageIndex, pageBlocks.OrderBy(Function(b) b.Item1).ToList())

                            For Each block In _blocks(pageIndex)
                                Dim blockText As String = text.Substring(block.Item2, block.Item3)

                                ' filter text  
                                Dim filteredText As String = blockText
                                For Each c In separators
                                    filteredText = filteredText.Replace(c, "")
                                Next
                                _fullText &= filteredText

                                charIndex = 0
                                For Each c In blockText.ToCharArray()
                                    If Not separators.Contains(c) Then
                                        _charPos.Add(totalCharIndex, New Tuple(Of Integer, Integer)(block.Item2 + charIndex, pageIndex))
                                        totalCharIndex += 1
                                    End If
                                    charIndex += 1
                                Next
                            Next

                            pageIndex += 1
                            charIndexOffset += text.Length
                        Next
                    Catch ex As Exception
                        appl.Dispatcher.BeginInvoke(
                            Sub()
                                Me.IsLoading = False
                                errorGrid.Visibility = Visibility.Visible
                                errorTextBlock.Text = Me.GetExceptionText(ex)
                                Me.InvalidateVisual()
                            End Sub)
                        Return
                    End Try

                    AddHandler _doc.FormFillEnvironment.Invalidate,
                        Sub(page As PdfPage, left As Double, top As Double, right As Double, bottom As Double)
                            Dim i As Integer = _doc.Pages.ToList().IndexOf(page)
                            _p(i).WritableBitmapForm = Nothing
                            Dim appl2 As Application = Application.Current
                            If Not appl2 Is Nothing Then
                                appl2.Dispatcher.BeginInvoke(
                                Sub()
                                    Debug.WriteLine("invalidate ln 846")
                                    Me.InvalidateVisual()
                                End Sub)
                            End If
                        End Sub

                    AddHandler _doc.FormFillEnvironment.OutputSelectedRect,
                        Sub(page As PdfPage, left As Double, top As Double, right As Double, bottom As Double)
                            Dim i As Integer = _doc.Pages.ToList().IndexOf(page)
                            Debug.WriteLine("outputSelectedRect")
                            _selectedRects.Add((pageIndex:=i, rect:=New FS_RECTF(left, top, right, bottom)))
                        End Sub

                    AddHandler _doc.FormFillEnvironment.SetCursor,
                        Sub(cursor As PDFiumSharp.CursorType)
                            Debug.WriteLine("SetCursor " & cursor.ToString())
                            Select Case cursor
                                Case PDFiumSharp.CursorType.Arrow : Me.Cursor = Cursors.Arrow
                                Case PDFiumSharp.CursorType.Hand : Me.Cursor = Cursors.Hand
                                Case PDFiumSharp.CursorType.HBeam : Me.Cursor = Cursors.IBeam
                                Case PDFiumSharp.CursorType.NESW : Me.Cursor = Cursors.SizeNESW
                                Case PDFiumSharp.CursorType.NWSE : Me.Cursor = Cursors.SizeNWSE
                                Case PDFiumSharp.CursorType.VBeam : Me.Cursor = Cursors.IBeam
                                Case Else : Me.Cursor = Nothing
                            End Select
                        End Sub

                    AddHandler _doc.FormFillEnvironment.GetCurrentPageIndex,
                        Function() As Integer
                            Return getCenteredPageIndex()
                        End Function

                    AddHandler _doc.FormFillEnvironment.Beep,
                        Sub(type As BeepType)
                            Select Case type
                                Case BeepType.Error : System.Media.SystemSounds.Asterisk.Play()
                                Case BeepType.Question : System.Media.SystemSounds.Question.Play()
                                Case BeepType.Warning : System.Media.SystemSounds.Exclamation.Play()
                                Case Else : System.Media.SystemSounds.Beep.Play()
                            End Select
                        End Sub

                    AddHandler _doc.FormFillEnvironment.AppAlert,
                        Function(msg As String, title As String, buttonType As ButtonType, iconType As IconType) As DialogResult
                            Dim appl2 As Application = Application.Current
                            Dim result As DialogResult = DialogResult.Cancel
                            If Not appl2 Is Nothing Then
                                appl2.Dispatcher.Invoke(
                                Sub()
                                    Dim button As MessageBoxButton
                                    Select Case buttonType
                                        Case buttonType.Ok : button = MessageBoxButton.OK
                                        Case buttonType.OkCancel : button = MessageBoxButton.OKCancel
                                        Case buttonType.YesNo : button = MessageBoxButton.YesNo
                                        Case buttonType.YesNoCancel : button = MessageBoxButton.YesNoCancel
                                    End Select

                                    Dim icon As MessageBoxImage
                                    Select Case iconType
                                        Case iconType.Error : icon = MessageBoxImage.Asterisk
                                        Case iconType.Information : icon = MessageBoxImage.Information
                                        Case iconType.Question : icon = MessageBoxImage.Question
                                        Case iconType.Warning : icon = MessageBoxImage.Warning
                                    End Select

                                    Select Case Me.OnAppAlert(msg, title, button, icon)
                                        Case MessageBoxResult.Cancel : result = DialogResult.Cancel
                                        Case MessageBoxResult.No : result = DialogResult.No
                                        Case MessageBoxResult.OK : result = DialogResult.Ok
                                        Case MessageBoxResult.Yes : result = DialogResult.Yes
                                    End Select
                                End Sub)
                            End If
                            Return result
                        End Function
                End SyncLock

                appl.Dispatcher.Invoke(
                    Sub()
                        For Each page In _doc.Pages
                            _p.Add(New PageData() With {
                                .Zoom = Me.Zoom,
                                .PageWidth = page.Width,
                                .PageHeight = page.Height
                            })
                        Next

                        scrollBarV.Visibility = Visibility.Visible
                        scrollBarH.Visibility = Visibility.Visible

                        If Me.DoResetOnNewDocument AndAlso Not _lastDocument Is Nothing AndAlso Not bytes.SequenceEqual(_lastDocument) Then
                            scrollBarV.Value = 0
                            scrollBarH.Value = 0
                            isFirst = True
                        End If
                        _lastDocument = bytes

                        If isFirst AndAlso _p.Count > 0 Then
                            If _p(0).Height > _p(0).Width Then
                                Me.FitToHeight()
                            Else
                                Me.FitToWidth()
                            End If
                        Else
                            resize()
                        End If

                        Debug.WriteLine("invalidate ln 954")
                        Me.InvalidateVisual()
                        Me.IsLoading = False
                    End Sub)
            End Sub))
        t.SetApartmentState(ApartmentState.STA)
        t.Start()
    End Sub

    Public Function Save() As Byte()
        Try
            ' take focus away from fields or the last edited field won't be saved
            _doc.Pages(0).Form.OnFocus(0, 0, 0)
        Catch ex As Exception
            ' v1 of the Pdfium.Windows libraries doesn't contain this function
        End Try

        ' save
        Using mem As MemoryStream = New MemoryStream()
            _doc.Save(mem, SaveFlags.NotIncremental)
            Return mem.GetBuffer()
        End Using
    End Function
#End Region
    Public Overridable Function OnAppAlert(message As String, caption As String, button As MessageBoxButton, icon As MessageBoxImage) As MessageBoxResult
        Return MessageBox.Show(message, caption, button, icon)
    End Function
#Region "Zoom Property"
    Public Shared ReadOnly ZoomProperty As DependencyProperty =
        DependencyProperty.Register("Zoom", GetType(Double), GetType(Viewer),
            New FrameworkPropertyMetadata(50.0, AddressOf OnZoomChanged) With {.BindsTwoWayByDefault = True})

    Public Property Zoom As Double
        Get
            Return GetValue(ZoomProperty)
        End Get
        Set(value As Double)
            SetValue(ZoomProperty, value)
        End Set
    End Property

    Private Overloads Shared Sub OnZoomChanged(ByVal d As DependencyObject, ByVal e As DependencyPropertyChangedEventArgs)
        CType(d, Viewer).OnZoomChanged()
    End Sub

    Public Overloads Sub OnZoomChanged()
        If Not _skip Then
            Me.ZoomTo(Me.Zoom, scrollBarH.ViewportSize / 2, scrollBarV.ViewportSize / 2)
        End If
    End Sub
#End Region
#Region "INotifyPropertyChanged implementation"
    Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged

    Private Sub NotifyPropertyChanged(ByVal propertyName As String)
        RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(propertyName))
    End Sub
#End Region

    Protected Overrides Sub OnRender(drawingContext As DrawingContext)
        Dim scrollbarVWidth As Integer = If(scrollBarV.Width = Double.NaN OrElse scrollBarV.Visibility <> Visibility.Visible, 0, scrollBarV.Width)
        Dim scrollbarHHeight As Integer = If(scrollBarH.Height = Double.NaN OrElse scrollBarH.Visibility <> Visibility.Visible, 0, scrollBarH.Height)
        drawingContext.PushClip(New RectangleGeometry(New Rect(0, 0, Me.ActualWidth, Me.ActualHeight)))
        drawingContext.DrawRectangle(Media.Brushes.Gray, New Media.Pen(), New Rect(0, 0, Me.ActualWidth, Me.ActualHeight))
        Dim dpi As DpiScale = VisualTreeHelper.GetDpi(Me)

        If Me.ActualWidth - scrollbarVWidth > 0 AndAlso Me.ActualHeight - scrollbarHHeight > 0 Then
            SyncLock _docLock
                If Not _p Is Nothing AndAlso _p.Count > 0 Then
                    ' find the first page to draw
                    Dim index As Integer = getFirstVisiblePageIndex()

                    ' get the number of pages to draw
                    Dim count As Integer = getNumberOfVisiblePages()

                    Debug.WriteLine("Drawing pages: index=" & index & ", count=" & count)

                    Dim normalizedSelection As TextRange = getNormalizedSelection()
                    Dim doSelectNextBlocks As Boolean =
                        normalizedSelection.EndPageIndex > normalizedSelection.StartPageIndex _
                        AndAlso normalizedSelection.StartPageIndex < index

                    ' draw pages
                    For i = index To index + count - 1
                        If i < _p.Count Then
                            Dim r As Rect = getPageRectangle(i)

                            If _p(i).WritableBitmapForm Is Nothing Then
                                Dim wbForm As WriteableBitmap = New WriteableBitmap(
                                        r.Width * dpi.PixelsPerInchX / 96, r.Height * dpi.PixelsPerInchY / 96, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Bgra32, Nothing)
                                _doc.Pages(i).RenderForm(wbForm)

                                Dim stride As Integer = Math.Abs(wbForm.BackBufferStride)
                                Dim bytes As Integer = stride * wbForm.PixelHeight
                                Dim rgbValues(bytes - 1) As Byte
                                Dim rgbValuesOriginal(bytes - 1) As Byte
                                Dim w As Integer = wbForm.PixelWidth, h As Integer = wbForm.PixelHeight

                                ' copy original bitmap
                                wbForm.CopyPixels(rgbValues, stride, 0)
                                wbForm.CopyPixels(rgbValuesOriginal, stride, 0)

                                For Each item In _selectedRects
                                    If item.pageIndex = i Then
                                        ' translate coordinates to device
                                        Dim p4 = _doc.Pages(i).PageToDevice((0, 0, wbForm.PixelWidth, wbForm.PixelHeight), item.rect.Left, item.rect.Top)
                                        Dim p5 = _doc.Pages(i).PageToDevice((0, 0, wbForm.PixelWidth, wbForm.PixelHeight), item.rect.Right, item.rect.Bottom)

                                        ' fill rectangle
                                        For x = p4.X To p5.X
                                            For y = p4.Y To p5.Y
                                                If x >= 0 AndAlso x < w AndAlso y >= 0 AndAlso y < h Then
                                                    Try
                                                        ' first put transparent areas on white background:
                                                        Dim t As Double = 1.0 * rgbValuesOriginal(4 * x + stride * y + 3) / 255 ' transparency Of pixel between 0 .. 1 , easier To Do math With this
                                                        Dim rt As Double = 1 - t ' inverted value of transparency
                                                        ' C = C * t + W * (1-t) // alpha transparency for this case C-color, W-white (255)
                                                        ' same for each color
                                                        rgbValuesOriginal(4 * x + stride * y + 0) = (rgbValuesOriginal(4 * x + stride * y + 0) * t + 255 * rt) ' R color
                                                        rgbValuesOriginal(4 * x + stride * y + 1) = (rgbValuesOriginal(4 * x + stride * y + 1) * t + 255 * rt) ' G color
                                                        rgbValuesOriginal(4 * x + stride * y + 2) = (rgbValuesOriginal(4 * x + stride * y + 2) * t + 255 * rt) ' B color
                                                        rgbValuesOriginal(4 * x + stride * y + 3) = 255 ' A = 255 => no transparency                                                         

                                                        ' now invert to blue
                                                        rgbValues(4 * x + stride * y + 0) = rgbValuesOriginal(4 * x + stride * y + 0)
                                                        rgbValues(4 * x + stride * y + 1) = 255 - rgbValuesOriginal(4 * x + stride * y + 1)
                                                        rgbValues(4 * x + stride * y + 2) = 255 - rgbValuesOriginal(4 * x + stride * y + 2)
                                                        rgbValues(4 * x + stride * y + 3) = rgbValuesOriginal(4 * x + stride * y + 3)
                                                    Catch ex As Exception
                                                        ' no idea why as of yet
                                                        Debug.WriteLine("unhandled ex: " & ex.Message)
                                                    End Try
                                                End If
                                            Next
                                        Next
                                    End If
                                Next

                                ' copy back
                                wbForm.WritePixels(
                                    New Int32Rect(0, 0, wbForm.PixelWidth, wbForm.PixelHeight),
                                    rgbValues, stride, 0)

                                _p(i).WritableBitmapForm = wbForm
                            End If

                            If _p(i).WritableBitmapPageOriginal Is Nothing Then
                                ' ...get page bitmap
                                _p(i).WritableBitmapPageOriginal = New WriteableBitmap(
                                        r.Width * dpi.PixelsPerInchX / 96, r.Height * dpi.PixelsPerInchY / 96, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Bgra32, Nothing)

                                ' white background
                                Dim stride As Integer = Math.Abs(_p(i).WritableBitmapPageOriginal.BackBufferStride)
                                Dim bytes As Integer = stride * _p(i).WritableBitmapPageOriginal.PixelHeight
                                Dim rgbValues(bytes - 1) As Byte
                                For j = 0 To rgbValues.Count - 1
                                    rgbValues(j) = 255
                                Next
                                _p(i).WritableBitmapPageOriginal.WritePixels(
                                    New Int32Rect(0, 0, _p(i).WritableBitmapPageOriginal.PixelWidth, _p(i).WritableBitmapPageOriginal.PixelHeight),
                                    rgbValues, stride, 0)

                                ' render page
                                _doc.Pages(i).RenderPage(_p(i).WritableBitmapPageOriginal)

                                _p(i).DoResetWritableBitmapPage = True
                            End If

                            If _p(i).WritableBitmapPage Is Nothing OrElse _p(i).DoResetWritableBitmapPage Then
                                _p(i).DoResetWritableBitmapPage = False

                                Dim stride As Integer = Math.Abs(_p(i).WritableBitmapPageOriginal.BackBufferStride)
                                Dim bytes As Integer = stride * _p(i).WritableBitmapPageOriginal.PixelHeight
                                Dim rgbValues(bytes - 1) As Byte
                                Dim rgbValuesOriginal(bytes - 1) As Byte

                                ' copy original bitmap
                                _p(i).WritableBitmapPageOriginal.CopyPixels(rgbValues, stride, 0)
                                _p(i).WritableBitmapPageOriginal.CopyPixels(rgbValuesOriginal, stride, 0)

                                If _p(i).WritableBitmapPage Is Nothing _
                                    OrElse r.Width <> _p(i).WritableBitmapPage.PixelWidth _
                                    OrElse r.Height <> _p(i).WritableBitmapPage.PixelHeight Then
                                    _p(i).WritableBitmapPage = New WriteableBitmap(
                                        r.Width * dpi.PixelsPerInchX / 96, r.Height * dpi.PixelsPerInchY / 96, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Bgra32, Nothing)
                                End If

                                Dim w As Integer = _p(i).WritableBitmapPage.PixelWidth, h As Integer = _p(i).WritableBitmapPage.PixelHeight

                                ' draw search highlighting
                                If Not _matches Is Nothing Then
                                    For Each range In _matches
                                        ' if the match is at least partially on this page...
                                        If i >= range.StartPageIndex AndAlso i <= range.EndPageIndex Then
                                            ' get (partial) highlight rects
                                            Dim numRects As Integer
                                            If i = range.StartPageIndex AndAlso i = range.EndPageIndex Then
                                                numRects = _doc.Pages(i).TextPage.CountRects(range.StartTextIndex, range.EndTextIndex - range.StartTextIndex)
                                            ElseIf i = range.StartPageIndex Then
                                                numRects = _doc.Pages(i).TextPage.CountRects(range.StartTextIndex, -1)
                                            ElseIf i = range.EndPageIndex Then
                                                numRects = _doc.Pages(i).TextPage.CountRects(0, range.EndTextIndex)
                                            Else
                                                numRects = _doc.Pages(i).TextPage.CountRects(0, -1)
                                            End If

                                            ' for each rect...
                                            For j = 0 To numRects - 1
                                                ' ...get coordinates
                                                Dim r2 As FS_RECTF = _doc.Pages(i).TextPage.GetRect(j)

                                                ' translate coordinates to device
                                                Dim p4 = _doc.Pages(i).PageToDevice((0, 0, _p(i).WritableBitmapPage.PixelWidth, _p(i).WritableBitmapPage.PixelHeight), r2.Left, r2.Top + 3)
                                                Dim p5 = _doc.Pages(i).PageToDevice((0, 0, _p(i).WritableBitmapPage.PixelWidth, _p(i).WritableBitmapPage.PixelHeight), r2.Right, r2.Bottom - 3)

                                                ' invert selection rectangle
                                                For x = p4.X To p5.X
                                                    For y = p4.Y To p5.Y
                                                        If x >= 0 AndAlso x < w AndAlso y >= 0 AndAlso y < h Then
                                                            Try
                                                                ' invert to yellow
                                                                rgbValues(4 * x + stride * y) = 255 - rgbValuesOriginal(4 * x + stride * y)
                                                                'rgbValues(4 * x + bmdPage.Stride * y + 1) = 255 - rgbValuesOriginal(4 * x + bmdPage.Stride * y + 1)
                                                                'rgbValues(4 * x + bmdPage.Stride * y + 2) = 255 - rgbValuesOriginal(4 * x + bmdPage.Stride * y + 2)
                                                                'rgbValues(4 * x + bmdPage.Stride * y + 3) = 255 - rgbValuesOriginal(4 * x + bmdPage.Stride * y + 3)  
                                                            Catch ex As Exception
                                                                ' no idea why as of yet
                                                                Debug.WriteLine("unhandled ex: " & ex.Message)
                                                            End Try
                                                        End If
                                                    Next
                                                Next
                                            Next
                                        End If
                                    Next
                                End If

                                ' if the selection is at least partially on this page...
                                If i >= normalizedSelection.StartPageIndex AndAlso i <= normalizedSelection.EndPageIndex AndAlso Me.Tool <> Tool.Form Then
                                    For Each block In _blocks(i)
                                        ' get (partial) selection rects
                                        Dim numRects As Integer = 0
                                        If i = normalizedSelection.StartPageIndex AndAlso i = normalizedSelection.EndPageIndex Then
                                            ' start selection page is end selection page
                                            If normalizedSelection.StartTextIndex >= block.Item2 And normalizedSelection.StartTextIndex <= block.Item2 + block.Item3 _
                                                AndAlso normalizedSelection.EndTextIndex >= block.Item2 And normalizedSelection.EndTextIndex <= block.Item2 + block.Item3 Then
                                                ' start and end selection is in this block
                                                numRects = _doc.Pages(i).TextPage.CountRects(normalizedSelection.StartTextIndex, normalizedSelection.EndTextIndex - normalizedSelection.StartTextIndex)
                                            ElseIf normalizedSelection.StartTextIndex >= block.Item2 And normalizedSelection.StartTextIndex <= block.Item2 + block.Item3 Then
                                                ' start selection is in this block
                                                numRects = _doc.Pages(i).TextPage.CountRects(normalizedSelection.StartTextIndex, block.Item2 + block.Item3 - normalizedSelection.StartTextIndex)
                                            ElseIf normalizedSelection.EndTextIndex >= block.Item2 And normalizedSelection.EndTextIndex <= block.Item2 + block.Item3 Then
                                                ' end selection is in this block
                                                numRects = _doc.Pages(i).TextPage.CountRects(block.Item2, normalizedSelection.EndTextIndex - block.Item2)
                                            Else
                                                Dim blockIndex As Integer = _blocks(i).IndexOf(block)
                                                If _blocks(i).Take(blockIndex).ToList().Exists(Function(b) normalizedSelection.StartTextIndex >= b.Item2 And normalizedSelection.StartTextIndex <= b.Item2 + b.Item3) _
                                                    AndAlso _blocks(i).TakeLast(_blocks(i).Count - blockIndex - 1).ToList().Exists(Function(b) normalizedSelection.EndTextIndex >= b.Item2 And normalizedSelection.EndTextIndex <= b.Item2 + b.Item3) Then
                                                    ' start is before this block and end is after this block
                                                    numRects = _doc.Pages(i).TextPage.CountRects(block.Item2, block.Item3)
                                                End If
                                            End If
                                        ElseIf i = normalizedSelection.StartPageIndex Then
                                            ' start selection is on this page
                                            If doSelectNextBlocks Then
                                                ' if we're past the start selection, select full block
                                                numRects = _doc.Pages(i).TextPage.CountRects(block.Item2, block.Item3)
                                            ElseIf normalizedSelection.StartTextIndex >= block.Item2 And normalizedSelection.StartTextIndex <= block.Item2 + block.Item3 Then
                                                ' selection starts in this block
                                                numRects = _doc.Pages(i).TextPage.CountRects(normalizedSelection.StartTextIndex, block.Item2 + block.Item3 - normalizedSelection.StartTextIndex)
                                                doSelectNextBlocks = True
                                            End If
                                        ElseIf i = normalizedSelection.EndPageIndex Then
                                            If normalizedSelection.EndTextIndex >= block.Item2 And normalizedSelection.EndTextIndex <= block.Item2 + block.Item3 Then
                                                ' selection ends in this block
                                                numRects = _doc.Pages(i).TextPage.CountRects(block.Item2, normalizedSelection.EndTextIndex - block.Item2)
                                                doSelectNextBlocks = False
                                            ElseIf doSelectNextBlocks Then
                                                ' if we're not past the end of the selection, select full block
                                                numRects = _doc.Pages(i).TextPage.CountRects(block.Item2, block.Item3)
                                            End If
                                        Else
                                            ' full page is selected
                                            numRects = _doc.Pages(i).TextPage.CountRects(block.Item2, block.Item3)
                                        End If

                                        ' for each rect...
                                        For j = 0 To numRects - 1
                                            ' ...get coordinates
                                            Dim r2 As FS_RECTF = _doc.Pages(i).TextPage.GetRect(j)

                                            ' translate coordinates to device
                                            Dim p4 = _doc.Pages(i).PageToDevice((0, 0, _p(i).WritableBitmapPage.PixelWidth, _p(i).WritableBitmapPage.PixelHeight), r2.Left, r2.Top + 3)
                                            Dim p5 = _doc.Pages(i).PageToDevice((0, 0, _p(i).WritableBitmapPage.PixelWidth, _p(i).WritableBitmapPage.PixelHeight), r2.Right, r2.Bottom - 3)

                                            ' invert selection rectangle
                                            For x = p4.X To p5.X
                                                For y = p4.Y To p5.Y
                                                    If x >= 0 AndAlso x < w AndAlso y >= 0 AndAlso y < h Then
                                                        Try
                                                            ' invert to blue
                                                            'rgbValues(4 * x + bmdPage.Stride * y) = 255 - rgbValuesOriginal(4 * x + bmdPage.Stride * y)
                                                            rgbValues(4 * x + stride * y + 1) = 255 - rgbValuesOriginal(4 * x + stride * y + 1)
                                                            rgbValues(4 * x + stride * y + 2) = 255 - rgbValuesOriginal(4 * x + stride * y + 2)
                                                            'rgbValues(4 * x + bmdPage.Stride * y + 3) = 255 - rgbValuesOriginal(4 * x + bmdPage.Stride * y + 3)  
                                                        Catch ex As Exception
                                                            ' no idea why as of yet
                                                            Debug.WriteLine("unhandled ex: " & ex.Message)
                                                        End Try
                                                    End If
                                                Next
                                            Next
                                        Next
                                    Next
                                End If

                                _p(i).WritableBitmapPage.WritePixels(
                                    New Int32Rect(0, 0, _p(i).WritableBitmapPage.PixelWidth, _p(i).WritableBitmapPage.PixelHeight),
                                    rgbValues, stride, 0)
                            End If

                            ' draw page bitmap
                            Dim sw2 As Stopwatch = New Stopwatch()
                            sw2.Start()

                            ' draw page border
                            drawingContext.DrawRectangle(Media.Brushes.Black, New Media.Pen(), New Rect(r.X + 3, r.Y + 3, r.Width, r.Height))

                            drawingContext.DrawImage(_p(i).WritableBitmapPage, New Rect(r.X, r.Y, r.Width, r.Height))
                            drawingContext.DrawImage(_p(i).WritableBitmapForm, New Rect(r.X, r.Y, r.Width, r.Height))

                            sw2.Stop()
                            Debug.WriteLine("page=" & If(_p(i).WritableBitmapPage Is Nothing, "nothing", "something"))
                            Debug.WriteLine("form=" & If(_p(i).WritableBitmapForm Is Nothing, "nothing", "something"))

                            Debug.WriteLine(String.Format("draw={0} x=" & r.X & " y=" & r.Y & " w=" & r.Width & " h=" & r.Height, sw2.Elapsed.ToString()))

                            Debug.WriteLine("-----")
                        End If
                    Next

                    _selectedRects.Clear()
                End If
            End SyncLock
        End If
    End Sub

    ''' <summary>
    ''' Turn around selection if we were selecting backwards.
    ''' </summary>
    ''' <returns>A TextRange object representing the normalized selection.</returns>
    Private Function getNormalizedSelection() As TextRange
        ' first get selection
        Dim startSelectionPage As Integer = _selection.StartPageIndex
        Dim startSelectionTextPos As Integer = _selection.StartTextIndex
        Dim endSelectionPage As Integer = _selection.EndPageIndex
        Dim endSelectionTextPos As Integer = _selection.EndTextIndex

        ' if we started selecting on a page further than where we stopped selecting...
        If startSelectionPage > endSelectionPage Then
            ' ...turn around selection
            startSelectionPage = _selection.EndPageIndex
            startSelectionTextPos = _selection.EndTextIndex
            endSelectionPage = _selection.StartPageIndex
            endSelectionTextPos = _selection.StartTextIndex
        ElseIf startSelectionPage = endSelectionPage AndAlso startSelectionPage <> -1 Then
            ' were selecting only on one page
            Dim sameBlock As Tuple(Of Integer, Integer, Integer, Integer) =
                _blocks(startSelectionPage).FirstOrDefault(
                    Function(b) startSelectionTextPos >= b.Item2 And startSelectionTextPos <= b.Item2 + b.Item3 _
                        AndAlso endSelectionTextPos >= b.Item2 And endSelectionTextPos <= b.Item2 + b.Item3)
            ' if we're selecting within one block...
            If Not sameBlock Is Nothing Then
                ' ...and start is greater than end selection ...
                If startSelectionTextPos > endSelectionTextPos Then
                    ' ...turn around selection
                    startSelectionTextPos = _selection.EndTextIndex
                    endSelectionTextPos = _selection.StartTextIndex
                End If
            Else
                ' we're selecting accross different blocks
                Dim startBlock As Tuple(Of Integer, Integer, Integer, Integer) =
                    _blocks(startSelectionPage).FirstOrDefault(
                        Function(b) startSelectionTextPos >= b.Item2 And startSelectionTextPos <= b.Item2 + b.Item3)
                Dim endBlock As Tuple(Of Integer, Integer, Integer, Integer) =
                    _blocks(startSelectionPage).FirstOrDefault(
                        Function(b) endSelectionTextPos >= b.Item2 And endSelectionTextPos <= b.Item2 + b.Item3)
                ' if the start selection block is greater than the end selection block...
                If _blocks(startSelectionPage).IndexOf(startBlock) > _blocks(startSelectionPage).IndexOf(endBlock) Then
                    ' ...turn around selection
                    startSelectionTextPos = _selection.EndTextIndex
                    endSelectionTextPos = _selection.StartTextIndex
                End If
            End If
        End If

        ' return textrange
        Return New TextRange() With {
            .StartPageIndex = startSelectionPage,
            .StartTextIndex = startSelectionTextPos,
            .EndPageIndex = endSelectionPage,
            .EndTextIndex = endSelectionTextPos
        }
    End Function

    Private Sub setCursor()
        Select Case Me.Tool
            Case Tool.Zoom
                Dim p As System.Windows.Point = Mouse.GetPosition(Me)
                Dim pageIndex As Integer = getPageByPoint(p)
                If p.X < 0 OrElse p.Y < 0 _
                    OrElse p.X > Me.ActualWidth - If(scrollBarV.Visibility = Visibility.Visible, scrollBarV.ActualWidth, 0) _
                    OrElse p.Y > Me.ActualHeight - If(scrollBarH.Visibility = Visibility.Visible, scrollBarH.ActualHeight, 0) Then
                    Me.Cursor = Nothing
                ElseIf pageIndex <> -1 Then
                    Me.Cursor = _zoomCursor
                Else
                    Me.Cursor = Nothing
                End If
            Case Tool.Select
                If _isSelecting Then
                    Me.Cursor = Cursors.IBeam
                Else
                    Me.Cursor = getSelectionCursor()
                End If
            Case Tool.Form
                Me.Cursor = Cursors.Arrow
        End Select
    End Sub

    Public Function getSelectionCursor() As Cursor
        Dim p As System.Windows.Point = Mouse.GetPosition(Me)
        If _isSelecting Then
            Return Cursors.IBeam
        ElseIf p.X < 0 OrElse p.Y < 0 _
            OrElse p.X > Me.ActualWidth - If(scrollBarV.Visibility = Visibility.Visible, scrollBarV.ActualWidth, 0) _
            OrElse p.Y > Me.ActualHeight - If(scrollBarH.Visibility = Visibility.Visible, scrollBarH.ActualHeight, 0) Then
            Return Nothing
        ElseIf Not _doc Is Nothing And Not _p Is Nothing Then
            Dim pageIndex As Integer = getPageByPoint(p)
            If pageIndex <> -1 Then
                ' translate point to bitmap
                Dim p4 = translatePointToPage(pageIndex, p)
                Dim p3 = _doc.Pages(pageIndex).DeviceToPage((0, 0, _doc.Pages(pageIndex).Width, _doc.Pages(pageIndex).Height), p4.X, p4.Y)
                Dim charIndex As Integer = _doc.Pages(pageIndex).TextPage.GetCharIndexAtPos(p3.X, p3.Y, 5, 5)
                Return If(charIndex = -1, Cursors.Arrow, Cursors.IBeam)
            Else
                Return Nothing
            End If
        Else
            Return Nothing
        End If
    End Function

    Private Function translatePointToPage(pageIndex As Integer, p As System.Windows.Point) As System.Windows.Point
        Dim r As Rect = getPageRectangle(pageIndex)
        Dim p2 As System.Windows.Point = New System.Windows.Point() With {
            .X = (p.X - r.X) / _p(pageIndex).Width * _p(pageIndex).Width,
            .Y = (p.Y - r.Y) / _p(pageIndex).Height * _p(pageIndex).Height
        }
        Dim p3 As System.Windows.Point = New System.Windows.Point With {
            .X = p2.X * _p(pageIndex).PageWidth / _p(pageIndex).Width,
            .Y = p2.Y * _p(pageIndex).PageHeight / _p(pageIndex).Height
        }
        Return p3
    End Function

    Private Function getPageByPoint(p As System.Windows.Point) As Integer
        SyncLock _docLock
            If Not _p Is Nothing AndAlso _p.Count > 0 Then
                ' find the first page to draw
                Dim index As Integer = getFirstVisiblePageIndex()

                ' get the number of pages to draw
                Dim count As Integer = Math.Ceiling(scrollBarV.ViewportSize / (_p(0).Height + PAGE_PADDING_Y)) + 1

                ' check if point is in page
                For i = index To index + count
                    If i < _p.Count Then
                        Dim r As Rect = getPageRectangle(i)
                        If (p.X = -1 OrElse (p.X > r.Left And p.X < r.Right)) And p.Y > r.Top And p.Y < r.Bottom Then
                            ' point is in page
                            Return i
                        End If
                    End If
                Next
            End If

            ' not on page
            Return -1
        End SyncLock
    End Function

    Protected Overrides Sub OnMouseWheel(e As MouseWheelEventArgs)
        scrollBarV.Value -= e.Delta
        Debug.WriteLine("invalidate ln 1338")
        Me.InvalidateVisual()
    End Sub

    Private Sub resize()
        Dim scrollBarVWidth As Double = 0
        Dim totalPageHeight As Double = If(Not _p Is Nothing, _p.Sum(Function(p) p.Height + PAGE_PADDING_Y), 0) + PAGE_PADDING_Y
        If Not _p Is Nothing AndAlso _p.Count > 0 AndAlso totalPageHeight > Me.ActualHeight Then
            scrollBarVWidth = scrollBarV.Width
            scrollBarV.Visibility = Visibility.Visible
        Else
            scrollBarV.Visibility = Visibility.Collapsed
        End If

        Dim scrollBarHHeight As Double = 0
        Dim maxPageWidth As Double = If(Not _p Is Nothing AndAlso _p.Count > 0, _p.Max(Function(p) p.Width + PAGE_PADDING_X), 0)
        If Not _p Is Nothing AndAlso _p.Count > 0 AndAlso maxPageWidth > Me.ActualWidth - scrollBarVWidth Then
            scrollBarHHeight = scrollBarH.Height
            scrollBarH.Visibility = Visibility.Visible
        Else
            scrollBarH.Visibility = Visibility.Collapsed
        End If

        If Me.ActualHeight > scrollBarHHeight Then
            scrollBarV.ViewportSize = Me.ActualHeight - scrollBarHHeight
            scrollBarV.LargeChange = scrollBarV.ViewportSize
            scrollBarV.SmallChange = 50
            If Not _p Is Nothing AndAlso _p.Count > 0 Then
                scrollBarV.Maximum = totalPageHeight - scrollBarV.ViewportSize
            Else
                scrollBarV.Maximum = 0
            End If
        End If

        If Me.ActualWidth > scrollBarVWidth Then
            scrollBarH.ViewportSize = Me.ActualWidth - scrollBarVWidth
            scrollBarH.LargeChange = scrollBarH.ViewportSize
            scrollBarH.SmallChange = 50
            If Not _p Is Nothing AndAlso _p.Count > 0 Then
                scrollBarH.Maximum = maxPageWidth - scrollBarH.ViewportSize
            Else
                scrollBarH.Maximum = 0
            End If
        End If
    End Sub

    Protected Overrides Sub OnRenderSizeChanged(sizeInfo As SizeChangedInfo)
        MyBase.OnRenderSizeChanged(sizeInfo)

        resize()
        Debug.WriteLine("invalidate ln 1388")
        Me.InvalidateVisual()
    End Sub

    Private Sub scrollBarV_Scroll(sender As Object, e As Primitives.ScrollEventArgs) Handles scrollBarV.Scroll
        Debug.WriteLine("invalidate ln 1393")
        Me.InvalidateVisual()
    End Sub

    Private Sub scrollBarH_Scroll(sender As Object, e As Primitives.ScrollEventArgs) Handles scrollBarH.Scroll
        Debug.WriteLine("invalidate ln 1398")
        Me.InvalidateVisual()
    End Sub

    Private Function getNumberOfVisiblePages() As Integer
        Dim scrollbarHHeight As Integer = If(scrollBarH.Height = Double.NaN OrElse scrollBarH.Visibility <> Visibility.Visible, 0, scrollBarH.Height)
        Dim count As Integer = 0
        For i = 0 To _p.Count - 1
            Dim r As Rect = getPageRectangle(i)
            If r.Y >= 0 AndAlso r.Y <= Me.ActualHeight - scrollbarHHeight _
                OrElse r.Y + r.Height >= 0 AndAlso r.Y + r.Height <= Me.ActualHeight - scrollbarHHeight _
                OrElse r.Y <= 0 AndAlso r.Y + r.Height >= Me.ActualHeight - scrollbarHHeight Then
                count += 1
            End If
        Next
        Return count
    End Function

    Private Function getFirstVisiblePageIndex() As Integer
        Dim scrollbarHHeight As Integer = If(scrollBarH.Height = Double.NaN OrElse scrollBarH.Visibility <> Visibility.Visible, 0, scrollBarH.Height)
        Dim count As Integer = 0
        For i = 0 To _p.Count - 1
            Dim r As Rect = getPageRectangle(i)
            If r.Y >= 0 AndAlso r.Y <= Me.ActualHeight - scrollbarHHeight _
                OrElse r.Y + r.Height >= 0 AndAlso r.Y + r.Height <= Me.ActualHeight - scrollbarHHeight _
                OrElse r.Y <= 0 AndAlso r.Y + r.Height >= Me.ActualHeight - scrollbarHHeight Then
                Return i
            End If
        Next
        Return -1
    End Function

    Private Function getCenteredPageIndex() As Integer
        Dim h As Double = scrollBarV.ViewportSize / 2
        Dim count As Integer = 0
        For i = 0 To _p.Count - 1
            Dim r As Rect = getPageRectangle(i)
            If r.Y - PAGE_PADDING_Y <= h AndAlso r.Y + r.Height >= h Then
                Return i
            End If
        Next
        Return -1
    End Function

    Private Const PAGE_PADDING_Y As Integer = 10
    Private Const PAGE_PADDING_X As Integer = 10

    Private Function getAbsolutePagePosition(i As Integer) As Point
        Dim y As Integer = 0
        For z = 0 To i - 1
            y += (_p(z).Height + PAGE_PADDING_Y)
        Next

        Dim maxWidth As Double = If(Not _p Is Nothing, _p.Max(Function(p) p.Width + PAGE_PADDING_X), 0)
        Dim x As Integer = maxWidth / 2 - _p(i).Width / 2

        Return New Point(x, y)
    End Function

    Private Function getPageRectangle(i As Integer) As Rect
        Dim scrollbarVWidth As Integer = If(scrollBarV.Width = Double.NaN OrElse scrollBarV.Visibility <> Visibility.Visible, 0, scrollBarV.Width)
        Dim scrollbarHHeight As Integer = If(scrollBarH.Height = Double.NaN OrElse scrollBarH.Visibility <> Visibility.Visible, 0, scrollBarH.Height)

        ' get y to draw i'th page
        Dim posY As Double = -scrollBarV.Value + PAGE_PADDING_Y
        For z = 0 To i - 1
            posY += (_p(z).Height + PAGE_PADDING_Y)
        Next

        ' get x to draw i'th page
        Dim maxWidth As Double = If(Not _p Is Nothing, _p.Max(Function(p) p.Width + PAGE_PADDING_X), 0)
        Dim x As Integer = maxWidth / 2 - _p(i).Width / 2
        If maxWidth < Me.ActualWidth - scrollbarVWidth Then
            x = x + (((Me.ActualWidth - scrollbarVWidth) - (maxWidth)) / 2) '+ (PAGE_PADDING_X / 2)
        Else
            x = x - scrollBarH.Value '+ (PAGE_PADDING_X / 2)
        End If

        Return New Rect(x, posY, _p(i).Width, _p(i).Height)
    End Function

    Public ReadOnly Property ZoomCursor As Cursor
        Get
            Return _zoomCursor
        End Get
    End Property

    Public Sub ZoomTo(zoom As Double, x As Double, y As Double)
        If zoom < 10 Then zoom = 10

        If Not _p Is Nothing AndAlso _p.Count > 0 Then
            Dim totalHBefore As Double = _p.Sum(Function(p) p.Height + PAGE_PADDING_Y)
            Dim maxWidth As Double = _p.Max(Function(p) p.Width + PAGE_PADDING_X)

            ' calc current percentages
            Dim vpct As Double = (scrollBarV.Value + y) / (totalHBefore) * 100
            Dim hpct As Double = (scrollBarH.Value + x) / (maxWidth) * 100

            ' recalc image sizes and gather total length
            Dim totalHeight As Decimal = 0
            Dim totalWidth As Decimal = 0
            For Each page In _p
                page.Zoom = zoom
                totalHeight += page.Height + PAGE_PADDING_Y
                If page.Width + PAGE_PADDING_X > totalWidth Then
                    totalWidth = page.Width + PAGE_PADDING_Y
                End If
                page.WritableBitmapPageOriginal = Nothing
                page.WritableBitmapForm = Nothing
            Next

            ' reset scrollbars
            resize()

            ' set scrollbars back to same percentage on new dimensions
            scrollBarV.Value = (totalHeight / 100 * vpct - y)
            scrollBarH.Value = (totalWidth / 100 * hpct - x)
        End If

        _skip = True
        Me.Zoom = zoom
        _skip = False

        resize()
        Debug.WriteLine("invalidate ln 1541")
        Me.InvalidateVisual()
    End Sub

    Public Sub FitToWidth()
        Dim i As Integer = getCenteredPageIndex()
        Dim z As Double = (scrollBarH.ViewportSize - scrollBarV.Width - 10) / (_p(i).OriginalWidth) * 100
        Me.ZoomTo(z, 0, 0)
        Dim p As Point = getAbsolutePagePosition(i)
        scrollBarH.Value = p.X
    End Sub

    Public Sub FitToHeight()
        Dim i As Integer = getCenteredPageIndex()
        Dim z As Double = (scrollBarV.ViewportSize - scrollBarH.Height - 10) / (_p(i).OriginalHeight) * 100
        Me.ZoomTo(z, 0, 0)
        Dim p As Point = getAbsolutePagePosition(i)
        scrollBarV.Value = p.Y
    End Sub

    Friend Class TextRange
        Public Property StartPageIndex As Integer = -1
        Public Property StartTextIndex As Integer
        Public Property EndPageIndex As Integer = -1
        Public Property EndTextIndex As Integer
    End Class

    Friend Class PageData
        Implements INotifyPropertyChanged

        Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged

        Private _zoom As Double = 100
        Private _originalWidth As Double
        Private _originalHeight As Double
        Private _pageWidth As Integer
        Private _pageHeight As Integer

        Public Property WritableBitmapPage As WriteableBitmap
        Public Property DoResetWritableBitmapPage As Boolean = True
        Public Property WritableBitmapPageOriginal As WriteableBitmap
        Public Property WritableBitmapForm As WriteableBitmap

        Public Property PageWidth As Integer
            Get
                Return _pageWidth
            End Get
            Set(value As Integer)
                _pageWidth = value
                _originalWidth = value * 4
            End Set
        End Property
        Public Property PageHeight As Integer
            Get
                Return _pageHeight
            End Get
            Set(value As Integer)
                _pageHeight = value
                _originalHeight = value * 4
            End Set
        End Property

        Public Property OriginalWidth As Double
            Get
                Return _originalWidth
            End Get
            Set(value As Double)
                _originalWidth = value
            End Set
        End Property

        Public Property OriginalHeight As Double
            Get
                Return _originalHeight
            End Get
            Set(value As Double)
                _originalHeight = value
            End Set
        End Property

        Public ReadOnly Property Width As Double
            Get
                Return Me.OriginalWidth / 100 * _zoom
            End Get
        End Property

        Public ReadOnly Property Height As Double
            Get
                Return Me.OriginalHeight / 100 * _zoom
            End Get
        End Property

        Public Property Zoom As Double
            Get
                Return _zoom
            End Get
            Set(value As Double)
                _zoom = value
                NotifyOfPropertyChange("Zoom")
                NotifyOfPropertyChange("Width")
            End Set
        End Property

        Public Property Cursor As Cursor

        Public Sub NotifyOfPropertyChange(<CallerMemberName> Optional name As String = Nothing)
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(name))
        End Sub
    End Class

    Protected Overridable Sub Dispose(disposing As Boolean)
        If Not disposedValue Then
            If disposing Then
                ' TODO: dispose managed state (managed objects)
                Debug.WriteLine("Disposing _doc")
                If Not _doc Is Nothing Then
                    CType(_doc, IDisposable).Dispose()
                End If
            End If

            ' TODO: free unmanaged resources (unmanaged objects) and override finalizer
            ' TODO: set large fields to null
            disposedValue = True
        End If
    End Sub

    ' ' TODO: override finalizer only if 'Dispose(disposing As Boolean)' has code to free unmanaged resources
    ' Protected Overrides Sub Finalize()
    '     ' Do not change this code. Put cleanup code in 'Dispose(disposing As Boolean)' method
    '     Dispose(disposing:=False)
    '     MyBase.Finalize()
    ' End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        ' Do not change this code. Put cleanup code in 'Dispose(disposing As Boolean)' method
        Dispose(disposing:=True)
        GC.SuppressFinalize(Me)
    End Sub
End Class