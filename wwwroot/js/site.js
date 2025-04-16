document.addEventListener('DOMContentLoaded', function () {
    console.log('Site JS loaded successfully');

    // Các chức năng toàn cục
    function handleAjaxErrors() {
        $(document).ajaxError(function (event, jqXHR, settings, thrownError) {
            console.error('AJAX Error:', thrownError);
            alert('Có lỗi xảy ra: ' + thrownError);
        });
    }

    handleAjaxErrors();
});