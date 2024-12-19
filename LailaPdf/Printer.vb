Imports System.Printing
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Documents
Imports System.Windows.Documents.Serialization
Imports System.Windows.Markup
Imports System.Windows.Media
Imports System.Windows.Media.Imaging
Imports System.Windows.Xps
Imports PDFiumSharp

Public Class Printer
    Public Event PrintProgress(sender As Object, e As PrintProgressEventArgs)
    Public Event PrintCompleted(sender As Object, e As EventArgs)

    Private Shared _lock As Object = New Object()

    Public Sub Print(displayName As String, bytes As Byte(), Optional printerName As String = Nothing,
                     Optional isTwoSided As Boolean? = True)
        Dim t As Thread = New Thread(New ThreadStart(
            Sub()
                Dim images As List(Of ImageSource) = New List(Of ImageSource)()

                SyncLock _lock
                    ' render document
                    Using pdfDocument = New PdfDocument(bytes, 0, bytes.Length)
                        RaiseEvent PrintProgress(Me, New PrintProgressEventArgs() With {
                            .TotalPages = pdfDocument.Pages.Count,
                            .CurrentPage = 0
                        })

                        For Each page In pdfDocument.Pages
                            Dim writableBitmap As WriteableBitmap = New WriteableBitmap(page.Width * 3, page.Height * 3, 96, 96, PixelFormats.Bgr32, Nothing)

                            ' white background
                            Dim stride As Integer = Math.Abs(writableBitmap.BackBufferStride)
                            Dim byteCount As Integer = stride * writableBitmap.PixelHeight
                            Dim rgbValues(byteCount - 1) As Byte
                            For j = 0 To rgbValues.Count - 1
                                rgbValues(j) = 255
                            Next
                            writableBitmap.WritePixels(
                            New Int32Rect(0, 0, writableBitmap.PixelWidth, writableBitmap.PixelHeight),
                            rgbValues, stride, 0)

                            ' render page
                            page.RenderPage(writableBitmap)
                            page.RenderForm(writableBitmap)

                            ' add to list
                            images.Add(writableBitmap)
                        Next
                    End Using
                End SyncLock

                ' get print queue and ticket
                Dim queue As PrintQueue
                If String.IsNullOrWhiteSpace(printerName) Then
                    queue = LocalPrintServer.GetDefaultPrintQueue()
                Else
                    queue = New LocalPrintServer().GetPrintQueues(
                        New EnumeratedPrintQueueTypes() {
                            EnumeratedPrintQueueTypes.Connections,
                            EnumeratedPrintQueueTypes.Local
                        }).FirstOrDefault(Function(q) q.FullName = printerName)
                End If
                If queue Is Nothing Then
                    Throw New Exception(String.Format("Printer '{0}' not found.", printerName))
                End If
                Dim ticket As PrintTicket = queue.DefaultPrintTicket.Clone()
                ticket.PageBorderless = PageBorderless.Borderless
                If isTwoSided.HasValue Then
                    ticket.Duplexing = If(isTwoSided.Value, Duplexing.TwoSidedLongEdge, Duplexing.OneSided)
                End If
                queue.UserPrintTicket = ticket
                queue.CurrentJobSettings.Description = displayName

                ' generate fixed document
                Dim fixedDocument As FixedDocument = New FixedDocument()
                For Each image In images
                    Dim page As FixedPage = New FixedPage() With {
                        .Width = If(image.Height > image.Width, ticket.PageMediaSize.Width, ticket.PageMediaSize.Height),
                        .Height = If(image.Height > image.Width, ticket.PageMediaSize.Height, ticket.PageMediaSize.Width),
                        .Margin = New Thickness(0)
                    }
                    page.Children.Add(New Image() With {
                        .Source = image,
                        .Width = If(image.Height > image.Width, ticket.PageMediaSize.Width, ticket.PageMediaSize.Height),
                        .Height = If(image.Height > image.Width, ticket.PageMediaSize.Height, ticket.PageMediaSize.Width),
                        .Margin = New Thickness(0)
                    })
                    Dim content As PageContent = New PageContent()
                    CType(content, IAddChild).AddChild(page)
                    fixedDocument.Pages.Add(content)
                Next

                Dim writer As XpsDocumentWriter = PrintQueue.CreateXpsDocumentWriter(queue)
                AddHandler writer.WritingPrintTicketRequired,
                    Sub(s As Object, e As WritingPrintTicketRequiredEventArgs)
                        e.CurrentPrintTicket = ticket

                        If e.CurrentPrintTicketLevel = Xps.Serialization.PrintTicketLevel.FixedPagePrintTicket Then
                            e.CurrentPrintTicket.PageOrientation =
                                If(images(e.Sequence - 1).Height > images(e.Sequence - 1).Width,
                                   PageOrientation.Portrait,
                                   PageOrientation.Landscape)

                            RaiseEvent PrintProgress(Me, New PrintProgressEventArgs() With {
                                .TotalPages = images.Count,
                                .CurrentPage = e.Sequence
                            })
                        End If
                    End Sub

                writer.Write(fixedDocument, ticket)

                RaiseEvent PrintCompleted(Me, New EventArgs())
            End Sub))

        t.SetApartmentState(ApartmentState.STA)
        t.Start()
    End Sub

    Public Class PrintProgressEventArgs
        Inherits EventArgs

        Public Property TotalPages As Integer
        Public Property CurrentPage As Integer
    End Class
End Class
