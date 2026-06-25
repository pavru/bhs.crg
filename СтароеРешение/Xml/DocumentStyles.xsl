<?xml version="1.0" encoding="UTF-8"?>
<xsl:stylesheet version="2.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:fo="http://www.w3.org/1999/XSL/Format" xmlns:ct="urn:BimHouse:CommonDataType" xmlns:cfg="urn:BimHouse:DocumentUtilConfig" xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xmlns:bf="urn:BimHouse:XslFunctions">
	<xsl:import href="file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/BimHouseFunctions.xsl"/>
	<xsl:output method="xml" version="1.0" encoding="UTF-8" indent="yes"/>

	<!--<xsl:template match="*[starts-with(@xsi:type,'ct:Тип.Базовый.Документ')]" mode="presenting">-->
	<xsl:template match="*[bf:InstanceOf(.,'urn:BimHouse:CommonDataType','Тип.Базовый.Документ')]" mode="presenting">
		<xsl:element name="{name()}" namespace="{namespace-uri()}">
			<xsl:copy-of select="@*"/>
			<xsl:apply-templates select="./node()" mode="#current"/>
			<xsl:apply-templates select="." mode="subtype-presenting"/>
			<xsl:element name="ct:Представления" namespace="urn:BimHouse:CommonDataType">
				<xsl:element name="ct:ТипНомерДатаТитулДоп" namespace="urn:BimHouse:CommonDataType">
					<xsl:call-template name="CreateDocTextpresentingType1">
						<xsl:with-param name="Doc" select="."/>
					</xsl:call-template>
				</xsl:element>
			</xsl:element>
		</xsl:element>
	</xsl:template>

	<xsl:template name="CreateDocTextpresentingType1">
		<xsl:param name="Doc"/>
		<xsl:if test="$Doc/ct:ТипДокумента != ''">
			<xsl:value-of select="$Doc/ct:ТипДокумента"/>
		</xsl:if>
		<xsl:if test="$Doc/ct:НомерДокумента != ''">
			<xsl:text> №&#160;</xsl:text>
			<xsl:value-of select="$Doc/ct:НомерДокумента"/>
		</xsl:if>
		<xsl:if test="$Doc/ct:ДатаДокумента">
			<xsl:text> от </xsl:text>
			<xsl:value-of select="format-date($Doc/ct:ДатаДокумента,'[D01].[M01].[Y0001]')"/>
		</xsl:if>
		<xsl:text>.</xsl:text>
		<xsl:if test="$Doc/ct:Титул != ''">
			<xsl:text> </xsl:text>
			<xsl:value-of select="$Doc/ct:Титул"/>
		</xsl:if>
		<xsl:if test="$Doc/ct:ДополнительнаяИнформация != ''">
			<xsl:text>, </xsl:text>
			<xsl:value-of select="$Doc/ct:ДополнительнаяИнформация"/>
		</xsl:if>
	</xsl:template>

</xsl:stylesheet>